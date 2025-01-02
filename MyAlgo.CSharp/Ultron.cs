#region imports
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Drawing;
using QuantConnect;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Portfolio.SignalExports;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Algorithm.Selection;
using QuantConnect.Api;
using QuantConnect.Parameters;
using QuantConnect.Benchmarks;
using QuantConnect.Brokerages;
using QuantConnect.Commands;
using QuantConnect.Configuration;
using QuantConnect.Util;
using QuantConnect.Interfaces;
using QuantConnect.Algorithm;
using QuantConnect.Indicators;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Custom;
using QuantConnect.Data.Custom.IconicTypes;
using QuantConnect.DataSource;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Data.Shortable;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Notifications;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Orders.OptionExercise;
using QuantConnect.Orders.Slippage;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Python;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.Option;
using QuantConnect.Securities.Positions;
using QuantConnect.Securities.Forex;
using QuantConnect.Securities.Crypto;
using QuantConnect.Securities.CryptoFuture;
using QuantConnect.Securities.IndexOption;
using QuantConnect.Securities.Interfaces;
using QuantConnect.Securities.Volatility;
using QuantConnect.Storage;
using QuantConnect.Statistics;
using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
using Calendar = QuantConnect.Data.Consolidators.Calendar;
#endregion

public class Ultron : QCAlgorithm
{
    enum TradeType
    {
        Buy, Sell
    }

    private decimal mMa3ma4DiffMaxVal;
    private decimal mMa1Ma2MinVal;
    private decimal mMa1Ma2MaxVal;
    private string direction = "Both"; // "Long" or "Short" or "Both"
    private Symbol symbol;
    private RollingWindow<QuoteBar> mBars;
    private int period1 = 7;
    private int period2 = 5;
    private int period3 = 32;
    private int period4 = 9;
    private decimal ma3Ma4DiffMaxPercent = 0.28m;
    private decimal ma1Ma2MinPercent = 0.05m;
    private decimal mMa1Ma2MaxPercent = 0.19m;
    private int takeProfitPips = 18;
    private int stopLossPips = 103;
    private IndicatorBase<IndicatorDataPoint> ma1;
    private IndicatorBase<IndicatorDataPoint> ma2;
    private IndicatorBase<IndicatorDataPoint> ma3;
    private IndicatorBase<IndicatorDataPoint> ma4;
    private DateTime mUtc;
    private int mDigits;

    public override void Initialize()
    {
        // Algorithm setup
        SetStartDate(2023, 11, 20); // Backtest start date
        SetEndDate(2024, 12, 29);   // Backtest end date
        SetCash(10_000);            // Starting cash

        // Add GBP/USD hourly data
        var cfd = AddCfd("GBP_USD", Resolution.Hour, Market.Dukascopy, true, 500);
        //var cfd = AddForex("GBPUSD", Resolution.Hour, Market.Oanda, true, 500);
        symbol = cfd.Symbol;

        mBars = new RollingWindow<QuoteBar>(5); // Store the last 2 bars

        // Initialize indicators
        ma1 = LWMA(symbol, period1, Resolution.Hour, Field.Open);
        ma2 = LWMA(symbol, period2, Resolution.Hour, Field.Close);
        ma3 = SMA(symbol, period3, Resolution.Hour, Field.Close);
        ma4 = SMA(symbol, period4, Resolution.Hour, Field.Close);

        mMa3ma4DiffMaxVal = ma3Ma4DiffMaxPercent / 100;
        mMa1Ma2MinVal = ma1Ma2MinPercent / 100;
        mMa1Ma2MaxVal = mMa1Ma2MaxPercent / 100;

        SetWarmUp(Math.Max(period1, Math.Max(period2, Math.Max(period3, period4))));
        var minPriceVariation = (double)Securities[symbol].SymbolProperties.MinimumPriceVariation;
        mDigits = (int)Math.Log10(1 / minPriceVariation);
    }

    public override void OnData(Slice data)
    {
        mUtc = data.UtcTime;

        // Add the current QuoteBar to the rolling window
        mBars.Add(data.QuoteBars[symbol]);

        if (IsWarmingUp)
            return;

        if (!ma1.IsReady || !ma2.IsReady || !ma3.IsReady || !ma4.IsReady || !mBars.IsReady)
            return;

        // Derived values
        var ma1ma2 = ma1.Current.Value - ma2.Current.Value;
        var ma2ma1 = ma2.Current.Value - ma1.Current.Value;
        var ma3ma4Diff = Math.Abs(ma3.Current.Value - ma4.Current.Value);

        // Ensure that trading conditions and direction are defined
        if ((direction == "Short" || direction == "Both") && !Portfolio.Invested)
        {
            /*
            && ma3ma4Diff < mMa3ma4DiffMaxVal
            && ma3.Current.Value > ma1.Current.Value
            && ma3.Current.Value > ma2.Current.Value
            && Bars.ClosePrices.Last(1) < Bars.ClosePrices.Last(2)
            && Bars.ClosePrices.Last(2) < Bars.OpenPrices.Last(2)
            && ma1ma2 < mMa1Ma2MaxVal
            && ma1ma2 > mMa1Ma2MinVal) */

            // Short trade conditions
            if (ma3ma4Diff < mMa3ma4DiffMaxVal)
                if (ma3.Current.Value > ma1.Current.Value)
                    if (ma3.Current.Value > ma2.Current.Value)
                        if (mBars[0].Bid.Close < mBars[1].Bid.Close)
                            if (mBars[1].Bid.Close < mBars[1].Bid.Open)
                                if (ma1ma2 < mMa1Ma2MaxVal)
                                    if (ma1ma2 > mMa1Ma2MinVal)
                                        PlaceOrder(TradeType.Sell);
        }

        if ((direction == "Long" || direction == "Both") && !Portfolio.Invested)
        {
            /*
            && ma3ma4Diff < mMa3ma4DiffMaxVal
            && ma3Value < ma1Value
            && ma3Value < ma2Value
            && Bars.ClosePrices.Last(1) > Bars.ClosePrices.Last(2)
            && Bars.ClosePrices.Last(2) > Bars.OpenPrices.Last(2)
            && ma2ma1 < mMa1Ma2MaxVal
            && ma2ma1 > mMa1Ma2MinVal) */

            // Long trade conditions
            if (ma3ma4Diff < mMa3ma4DiffMaxVal)
                if (ma3.Current.Value < ma1.Current.Value)
                    if (ma3.Current.Value < ma2.Current.Value)
                        if (mBars[0].Ask.Close > mBars[1].Ask.Close)
                            if (mBars[1].Ask.Close > mBars[1].Ask.Open)
                                if (ma2ma1 < mMa1Ma2MaxVal)
                                    if (ma2ma1 > mMa1Ma2MinVal)
                                        PlaceOrder(TradeType.Buy);
        }
    }

    private void PlaceOrder(TradeType tradeType)
    {
        var quantity = CalculateOrderQuantity(symbol, 10.0);  // Example fixed quantity
        decimal stopLossPrice = 0;
        decimal takeProfitPrice = 0;

        OrderTicket marketOrder = null;
        if (tradeType == TradeType.Buy)
        {
            stopLossPrice = Securities[symbol].BidPrice - stopLossPips * 0.0001m;
            takeProfitPrice = Securities[symbol].BidPrice + takeProfitPips * 0.0001m;
            marketOrder = MarketOrder(symbol, quantity);
        }
        else if (tradeType == TradeType.Sell)
        {
            stopLossPrice = Securities[symbol].AskPrice + stopLossPips * 0.0001m;
            takeProfitPrice = Securities[symbol].AskPrice - takeProfitPips * 0.0001m;
            marketOrder = MarketOrder(symbol, -quantity);
        }

        var stopLossOrder = StopMarketOrder(symbol, -marketOrder.Quantity, stopLossPrice);
        Math.Round(stopLossPrice, mDigits);

        var takeProfitOrder = LimitOrder(symbol, -marketOrder.Quantity, takeProfitPrice);
        Math.Round(takeProfitPrice, mDigits);
    }

    public override void OnOrderEvent(OrderEvent orderEvent)
    {
        mUtc = orderEvent.UtcTime;

        if (orderEvent.Status == OrderStatus.Submitted)
            return;

        var order = Transactions.GetOrderById(orderEvent.OrderId);
        if (orderEvent.Status == OrderStatus.Filled)
            if (order.Type == OrderType.StopMarket
                || order.Type == OrderType.Limit)
            {
                var openOrders = Transactions.GetOpenOrders();
                foreach (var openOrder in openOrders)
                {
                    if (openOrder.Type == OrderType.StopMarket
                        || openOrder.Type == OrderType.Limit)
                    {
                        Transactions.CancelOrder(openOrder.Id);
                    }
                }
            }
    }

    public override void OnEndOfAlgorithm()
    {
        Log("Algorithm completed.");
    }
}
