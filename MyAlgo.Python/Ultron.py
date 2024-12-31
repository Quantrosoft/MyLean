import numpy as np
from AlgorithmImports import *


class Ultron(QCAlgorithm):
    status: str = "IDLE"

    def Initialize(self):
        # Algorithm setup
        self.SetStartDate(2024, 7, 1)  # Backtest start date
        self.SetEndDate(2025, 1, 1)  # Backtest end date
        self.SetCash(10_000)  # Starting cash

        # Add GBP/USD hourly data
        self.symbol = self.add_cfd(
            "GBP_USD", Resolution.Hour, Market.DUKASCOPY, True, 500
        ).Symbol

        # Parameters
        self.direction = "Both"  # "Long" or "Short" or "Both"
        self.period1 = 5
        self.period2 = 3
        self.period3 = 28
        self.period4 = 9
        self.ma3_ma4_diff_max_percent = 0.28
        self.ma1_ma2_min_percent = 0.07
        self.ma1_ma2_max_percent = 0.24
        self.take_profit_pips = 20
        self.stop_loss_pips = 122

        # Initialize indicators
        self.ma1 = self.lwma(self.symbol, self.period1, Resolution.Hour, Field.Open)
        self.ma2 = self.lwma(self.symbol, self.period2, Resolution.Hour, Field.Close)
        self.ma3 = self.sma(self.symbol, self.period3, Resolution.Hour, Field.Close)
        self.ma4 = self.sma(self.symbol, self.period4, Resolution.Hour, Field.Close)

        self.status = "IDLE"  # STATUS_IDLE or STATUS_TRADING
        pass

    def OnData(self, data):
        if (
            not self.ma1.IsReady
            or not self.ma2.IsReady
            or not self.ma3.IsReady
            or not self.ma4.IsReady
        ):
            return

        # Current indicator values
        ma1_value = self.ma1.Current.Value
        ma2_value = self.ma2.Current.Value
        ma3_value = self.ma3.Current.Value
        ma4_value = self.ma4.Current.Value

        # Derived values
        ma1_ma2_diff = ma1_value - ma2_value
        ma3_ma4_diff = abs(ma3_value - ma4_value)

        # Ensure that trading conditions and direction are defined
        if self.direction in ["Short", "Both"] and self.status == "IDLE":
            # Short trade conditions
            if (
                ma3_ma4_diff < self.ma3_ma4_diff_max_percent
                and ma3_value > ma1_value
                and ma3_value > ma2_value
                and data[self.symbol].Close
                < data[self.symbol].Open  # Price action check
                and self.ma1_ma2_min_percent < ma1_ma2_diff < self.ma1_ma2_max_percent
            ):
                self.PlaceOrder(TradeType.Sell)

        if self.direction in ["Long", "Both"] and self.status == "IDLE":
            # Long trade conditions
            if (
                ma3_ma4_diff < self.ma3_ma4_diff_max_percent
                and ma3_value < ma1_value
                and ma3_value < ma2_value
                and data[self.symbol].Close
                > data[self.symbol].Open  # Price action check
                and self.ma1_ma2_min_percent < -ma1_ma2_diff < self.ma1_ma2_max_percent
            ):
                self.PlaceOrder(TradeType.Buy)

    def PlaceOrder(self, trade_type):
        quantity = self.CalculateOrderQuantity(
            self.symbol, 0.01
        )  # Example fixed quantity
        stop_loss_price = None
        take_profit_price = None

        if trade_type == TradeType.Buy:
            stop_loss_price = (
                self.Securities[self.symbol].Price - self.stop_loss_pips * 0.0001
            )
            take_profit_price = (
                self.Securities[self.symbol].Price + self.take_profit_pips * 0.0001
            )
            self.MarketOrder(self.symbol, quantity)
        elif trade_type == TradeType.Sell:
            stop_loss_price = (
                self.Securities[self.symbol].Price + self.stop_loss_pips * 0.0001
            )
            take_profit_price = (
                self.Securities[self.symbol].Price - self.take_profit_pips * 0.0001
            )
            self.MarketOrder(self.symbol, -quantity)

        self.status = "TRADING"

        # Log trade
        self.Debug(
            f"Placed {trade_type.name} order: Quantity={quantity}, \
            StopLoss={stop_loss_price}, TakeProfit={take_profit_price}"
        )

    def OnOrderEvent(self, orderEvent):
        if orderEvent.Status == OrderStatus.Filled:
            self.status = "IDLE"

    def OnEndOfAlgorithm(self):
        self.Log("Algorithm completed.")
