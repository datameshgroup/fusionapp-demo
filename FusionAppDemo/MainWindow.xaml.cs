﻿using DataMeshGroup.Fusion;
using DataMeshGroup.Fusion.Model;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FusionAppDemo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IMessageParser messageParser;
        private readonly HttpClient httpClient;
        private readonly List<SaleItem> basket;
        private List<SaleItem> productDatabase;

        private string saleId;
        private readonly bool initialised;


        public MainWindow()
        {
            initialised = false;
            InitializeComponent();

            messageParser = new NexoMessageParser() { EnableMACValidation = false };
            basket = [];
            httpClient = new()
            {
                BaseAddress = new Uri("http://localhost:4242/fusion/v1/")
            };
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            httpClient.DefaultRequestHeaders.Add("x-application-name", "MPOS");
            httpClient.DefaultRequestHeaders.Add("x-software-version", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");

            // Add some demo products
            UpdateProductDatabase();
            
            initialised = true;
            NewSale();
        }


        private async void BtnTender_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await PerformPayment();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing payment\n\n{ex.Message}");
                throw;
            }

            // New sale
            NewSale();
        }

        public async Task PerformPayment()
        {
            // Payment params
            string sessionId = Guid.NewGuid().ToString(); // unique sessionId per request
            _ = decimal.TryParse(TxtCashout.Text, out decimal cashoutAmount); // cashout amount
            decimal requestedAmount = basket.Sum(si => si.ItemAmount) + cashoutAmount;
            PaymentType paymentType = PaymentType.Normal;
            string saleId = LblSaleId.Content.ToString() ?? "";

            // Check that we have items or cashout
            if (requestedAmount == 0)
            {
                throw new Exception("Must add items or cashout");
            }
            // Check if this is just a cashout
            else if (requestedAmount == cashoutAmount)
            {
                paymentType = PaymentType.CashAdvance;
            }
            // Check if this is a purchase + cash
            else if (cashoutAmount > 0)
            {
                basket.Add(new SaleItem()
                {
                    ItemID = basket.Count,
                    ProductCode = "CASHOUT",
                    UnitOfMeasure = UnitOfMeasure.Other,
                    Quantity = 1,
                    UnitPrice = cashoutAmount,
                    ItemAmount = cashoutAmount,
                    ProductLabel = "CASHOUT",
                    Categories = ["CASHOUT"],
                    CustomFields = []
                });
            }

            // Construct payment request
            PaymentRequest paymentRequest = new(saleId, requestedAmount, basket, paymentType);
            paymentRequest.PaymentTransaction.AmountsReq.CashBackAmount = cashoutAmount;

            // Build json and send/recv
            string? requestJson = messageParser.MessagePayloadToString(paymentRequest) ?? throw new Exception("Error building request message");
            HttpRequestMessage httpRequestMessage = new(HttpMethod.Post, $"payments/{sessionId}?events=true")
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            CancellationTokenSource cts = new(TimeSpan.FromSeconds(300));

            HttpResponseMessage httpResponseMessage = await httpClient.SendAsync(httpRequestMessage, cts.Token);
            httpResponseMessage.EnsureSuccessStatusCode();
            PaymentResponse? paymentResponse = null;
            try
            {
                bool awaitingResponse = true;
                do
                {
                    httpResponseMessage = await httpClient.GetAsync($"payments/{sessionId}/events", cts.Token);
                    httpResponseMessage.EnsureSuccessStatusCode();

                    string responseJson = await httpResponseMessage.Content.ReadAsStringAsync(cts.Token);

                    SaleToPOIMessage saleToPOIMessage = messageParser.ParseSaleToPOIMessage(responseJson);

                    switch (saleToPOIMessage.MessagePayload)
                    {
                        case PaymentResponse r:
                            awaitingResponse = false;
                            paymentResponse = r;
                            break;
                        case PrintRequest r:
                            // Check if we need to send this receipt to the printer now
                            if (r.PrintOutput.RequiredSignatureFlag ?? false)
                            {
                                MessageBox.Show($"RECEIPT TO PRINT\n\n{r.GetReceiptAsPlainText()}"); // Send receipt to printer
                            }
                            break;
                        default:
                            break;
                    }

                }
                while (awaitingResponse);
            }
            catch (Exception)
            {
                paymentResponse = await PerformPaymentErrorHandling(sessionId, cts.Token);
            }

            if(paymentResponse == null)
            {
                throw new Exception($"ERROR: Could not get payment result from FusionApp. Please check the terminal for the payment result");
            }

            // Handle result
            MessageBox.Show($"TRANSACTION COMPLETE\n\nRESULT:{paymentResponse.Response.Result}\n\nRECEIPT TO PRINT\n{paymentResponse.GetReceiptAsPlainText()}");
        }

        public async Task<PaymentResponse?> PerformPaymentErrorHandling(string sessionId, CancellationToken cancellationToken)
        {
            int retryCount = 0;
            do
            {
                try
                {
                    HttpResponseMessage httpResponseMessage = await httpClient.GetAsync($"payments/{sessionId}", cancellationToken);
                    httpResponseMessage.EnsureSuccessStatusCode(); 
                
                    string responseJson = await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken); // Content is { PaymentResponse }
                    return messageParser.ParseMessagePayload(MessageCategory.Payment, MessageType.Response, responseJson) as PaymentResponse;
                }
                catch(Exception)
                {
                    await Task.Delay(5000, cancellationToken); // FusionApp could have crashed. Give it some time to restart
                }
                retryCount++;
            }
            while (retryCount < 3);

            // Critical error when error handling fails. Present screen to the cashier to check the terminal for the payment result            
            return null;
        }

        private void NewSale()
        {
            saleId = DateTime.Now.ToString("yyyyMMddHHmmss");
            TxtCashout.Text = "0.00";
            basket.Clear();
            UpdateUI();
        }


        #region UI HANDLING

        private void UpdateUI()
        {
            if(!initialised) return;

            LblSaleId.Content = saleId;

            decimal total = basket.Sum(si => si.ItemAmount);
            LblSubTotal.Content = total.ToString("C");
            if (decimal.TryParse(TxtCashout.Text, out decimal cashout))
            {
                total += cashout;
            }
            LblTotal.Content = total.ToString("C");


            LboSaleItems.Items.Clear();
            foreach (SaleItem si in basket)
            {
                LboSaleItems.Items.Add($"{si.ProductLabel,-20} {si.UnitPrice,-10:C} {si.Quantity,-10} {si.ItemAmount,-10:C}");
            }
        }

        private void UpdateProductDatabase()
        {
            productDatabase =
            [
                new()
                {
                    ItemID = 0,
                    ProductCode = "hWqJV4r44v9Q",
                    UnitOfMeasure = UnitOfMeasure.Litre,
                    Quantity = 7.62M,
                    UnitPrice = 1.97900262M,
                    ItemAmount = 15.08M,
                    ProductLabel = "SHELL UNLEADED E10",
                    Categories = ["Fuel", "Unleaded"],
                    CustomFields =
                    [
                        new("FuelProductCode", "1"),
                        new("FuelProductCodeShellCard", "1")
                    ]
                },
                new()
                {
                    ItemID = 1,
                    ProductCode = "bu8tQ26Puym4",
                    UnitOfMeasure = UnitOfMeasure.Other,
                    EanUpc = "300011801",
                    Quantity = 1M,
                    UnitPrice = 55.99M,
                    ItemAmount = 55.99M,
                    ProductLabel = "AdBlue 10L",
                    Categories = ["ADBLUE"],
                    CustomFields =
                    [
                        new("FuelProductCode", "19"),
                        new("FuelProductCodeShellCard", "6")
                    ]
                },
                new()
                {
                    ItemID = 3,
                    ProductCode = "sja6K3TX5g7z",
                    UnitOfMeasure = UnitOfMeasure.Other,
                    EanUpc = "9300675000628",
                    Quantity = 1M,
                    UnitPrice = 4.25M,
                    ItemAmount = 4.25M,
                    ProductLabel = "Coca Cola 375mL",
                    Categories = ["Store", "Drinks"],
                    CustomFields =
                    [
                        new("FuelProductCode", "13"),
                        new("FuelProductCodeShellCard", "48")
                    ]
                },
            ];
        }



        private void BtnAddProduct_Click(object sender, RoutedEventArgs e)
        {
            int index = int.Parse(((Button)sender).Tag.ToString() ?? "0");
            AddToBasket(productDatabase[index]);
            UpdateUI();
        }
        private void AddToBasket(SaleItem saleItem)
        {
            // Check if item already in basket
            SaleItem? item = basket.FirstOrDefault(si => si.ProductCode == saleItem.ProductCode);

            if (item != null)
            {
                item.Quantity += saleItem.Quantity;
                item.ItemAmount += saleItem.ItemAmount;
                return;
            }

            item = new()
            {
                ItemID = basket.Count,
                ProductCode = saleItem.ProductCode,
                UnitOfMeasure = saleItem.UnitOfMeasure,
                EanUpc = saleItem.EanUpc,
                Quantity = saleItem.Quantity,
                UnitPrice = saleItem.UnitPrice,
                ItemAmount = saleItem.ItemAmount,
                ProductLabel = saleItem.ProductLabel,
                Categories = saleItem.Categories,
                CustomFields = saleItem.CustomFields
            };
            basket.Add(item);
        }

        private void BtnNewSale_Click(object sender, RoutedEventArgs e)
        {
            NewSale();
        }
        private void TxtCashout_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateUI();
        }

        #endregion
    }
}

