import math
from AlgorithmImports import *


class Ultron(QCAlgorithm):
    class TradeType:
        Buy = "Buy"
        Sell = "Sell"

    def Initialize(self):
        # Algorithm setup
        self.SetStartDate(2023, 11, 20)  # Backtest start date
        self.SetEndDate(2024, 12, 29)  # Backtest end date
        self.SetCash(10000)  # Starting cash

        # Add GBP/USD hourly data
        cfd = self.AddCfd("GBP_USD", Resolution.Hour, Market.Dukascopy, True, 500)
        # cfd = self.AddForex("GBPUSD", Resolution.Hour, Market.Oanda, True, 500)
        self.symbol = cfd.Symbol

        self.mBars = RollingWindow[QuoteBar](5)

        # Initialize parameters and indicators
        self.period1 = 7
        self.period2 = 5
        self.period3 = 32
        self.period4 = 9
        self.ma3Ma4DiffMaxPercent = 0.28
        self.ma1Ma2MinPercent = 0.05
        self.ma1Ma2MaxPercent = 0.19
        self.takeProfitPips = 18
        self.stopLossPips = 103

        self.ma1 = self.LWMA(self.symbol, self.period1, Resolution.Hour, Field.Open)
        self.ma2 = self.LWMA(self.symbol, self.period2, Resolution.Hour, Field.Close)
        self.ma3 = self.SMA(self.symbol, self.period3, Resolution.Hour, Field.Close)
        self.ma4 = self.SMA(self.symbol, self.period4, Resolution.Hour, Field.Close)

        self.mMa3ma4DiffMaxVal = self.ma3Ma4DiffMaxPercent / 100
        self.mMa1Ma2MinVal = self.ma1Ma2MinPercent / 100
        self.mMa1Ma2MaxVal = self.ma1Ma2MaxPercent / 100

        self.SetWarmUp(max(self.period1, self.period2, self.period3, self.period4))
        self.mDigits = int(
            math.log10(
                1
                / float(
                    self.Securities[self.symbol].SymbolProperties.MinimumPriceVariation
                )
            )
        )

        self.direction = "Both"

    def OnData(self, data):
        self.mUtc = data.UtcTime

        # Add the current QuoteBar to the rolling window
        if self.symbol in data.QuoteBars:
            self.mBars.Add(data.QuoteBars[self.symbol])

        if self.IsWarmingUp or not (
            self.ma1.IsReady
            and self.ma2.IsReady
            and self.ma3.IsReady
            and self.ma4.IsReady
            and self.mBars.IsReady
        ):
            return

        ma1ma2 = self.ma1.Current.Value - self.ma2.Current.Value
        ma2ma1 = self.ma2.Current.Value - self.ma1.Current.Value
        ma3ma4Diff = abs(self.ma3.Current.Value - self.ma4.Current.Value)

        # Short trade conditions
        if (self.direction in ["Short", "Both"]) and not self.Portfolio.Invested:
            if (
                ma3ma4Diff < self.mMa3ma4DiffMaxVal
                and self.ma3.Current.Value > self.ma1.Current.Value
                and self.ma3.Current.Value > self.ma2.Current.Value
                and self.mBars[0].Bid.Close < self.mBars[1].Bid.Close
                and self.mBars[1].Bid.Close < self.mBars[1].Bid.Open
                and self.mMa1Ma2MinVal < ma1ma2 < self.mMa1Ma2MaxVal
            ):
                self.PlaceOrder(self.TradeType.Sell)

        # Long trade conditions
        if (self.direction in ["Long", "Both"]) and not self.Portfolio.Invested:
            if (
                ma3ma4Diff < self.mMa3ma4DiffMaxVal
                and self.ma3.Current.Value < self.ma1.Current.Value
                and self.ma3.Current.Value < self.ma2.Current.Value
                and self.mBars[0].Ask.Close > self.mBars[1].Ask.Close
                and self.mBars[1].Ask.Close > self.mBars[1].Ask.Open
                and self.mMa1Ma2MinVal < ma2ma1 < self.mMa1Ma2MaxVal
            ):
                self.PlaceOrder(self.TradeType.Buy)

    def PlaceOrder(self, tradeType):
        quantity = self.CalculateOrderQuantity(self.symbol, 10.0)
        stopLossPrice, takeProfitPrice = 0, 0

        if tradeType == self.TradeType.Buy:
            stopLossPrice = (
                self.Securities[self.symbol].BidPrice - self.stopLossPips * 0.0001
            )
            takeProfitPrice = (
                self.Securities[self.symbol].BidPrice + self.takeProfitPips * 0.0001
            )
            marketOrder = self.MarketOrder(self.symbol, quantity)
        elif tradeType == self.TradeType.Sell:
            stopLossPrice = (
                self.Securities[self.symbol].AskPrice + self.stopLossPips * 0.0001
            )
            takeProfitPrice = (
                self.Securities[self.symbol].AskPrice - self.takeProfitPips * 0.0001
            )
            marketOrder = self.MarketOrder(self.symbol, -quantity)

        self.StopMarketOrder(
            self.symbol, -marketOrder.Quantity, round(stopLossPrice, self.mDigits)
        )

        self.LimitOrder(
            self.symbol, -marketOrder.Quantity, round(takeProfitPrice, self.mDigits)
        )

    def OnOrderEvent(self, orderEvent):
        self.mUtc = orderEvent.UtcTime

        if orderEvent.Status == OrderStatus.Submitted:
            return

        order = self.Transactions.GetOrderById(orderEvent.OrderId)
        if orderEvent.Status == OrderStatus.Filled and order.Type in [
            OrderType.StopMarket,
            OrderType.Limit,
        ]:
            openOrders = self.Transactions.GetOpenOrders()
            for openOrder in openOrders:
                if openOrder.Type in [OrderType.StopMarket, OrderType.Limit]:
                    self.Transactions.CancelOrder(openOrder.Id)

    def OnEndOfAlgorithm(self):
        self.Log("Algorithm completed.")
