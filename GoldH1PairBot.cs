using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GoldH1PairBot : Robot
    {
        [Parameter("Symbol", DefaultValue = "XAUUSD")]
        public string TradeSymbol { get; set; }

        [Parameter("Volume (Lots)", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double VolumeLots { get; set; }

        [Parameter("Profit Target (USD)", DefaultValue = 15.0)]
        public double ProfitTargetUsd { get; set; }

        [Parameter("Loss Limit (USD)", DefaultValue = 5.0)]
        public double LossLimitUsd { get; set; }

        [Parameter("Protected Profit (USD)", DefaultValue = 0.70)]
        public double ProtectedProfitUsd { get; set; }

        [Parameter("Total Profit Stop (USD)", DefaultValue = 50.0)]
        public double TotalProfitStopUsd { get; set; }

        [Parameter("Bot Label", DefaultValue = "GoldH1PairBot")]
        public string BotLabel { get; set; }

        private Symbol _symbol;
        private double _volumeInUnits;
        private DateTime _currentH1Bar;
        private double _sessionProfit;
        private bool _stopping;

        protected override void OnStart()
        {
            _symbol = Symbols.GetSymbol(TradeSymbol);

            if (_symbol == null)
            {
                Print("ERROR: Symbol not found: {0}", TradeSymbol);
                Stop();
                return;
            }

            _volumeInUnits = _symbol.QuantityToVolumeInUnits(VolumeLots);
            _currentH1Bar = Bars.OpenTimes.LastValue;

            Positions.Closed += OnPositionClosed;

            Print("======================================");
            Print("GoldH1PairBot STARTED");
            Print("Symbol: {0}", _symbol.Name);
            Print("Volume: {0} lots", VolumeLots);
            Print("Profit Target: ${0}", ProfitTargetUsd);
            Print("Loss Limit: ${0}", LossLimitUsd);
            Print("Protected Profit: ${0}", ProtectedProfitUsd);
            Print("Total Profit Stop: ${0}", TotalProfitStopUsd);
            Print("H1 Timeframe");
            Print("======================================");
        }

        protected override void OnTick()
        {
            if (_stopping)
                return;

            CheckForNewH1Candle();
            ManagePositions();
            CheckTotalProfit();
        }

        private void CheckForNewH1Candle()
        {
            DateTime latestBar = Bars.OpenTimes.LastValue;

            if (latestBar <= _currentH1Bar)
                return;

            _currentH1Bar = latestBar;

            Print("--------------------------------------");
            Print("NEW H1 CANDLE: {0}", _currentH1Bar);
            Print("--------------------------------------");

            // Close all remaining trades from the previous H1 candle.
            foreach (var position in GetBotPositions().ToArray())
            {
                double profit = position.NetProfit;

                var result = ClosePosition(position);

                Print(
                    "H1 CLOSE: {0} position closed. P/L=${1:F2}, Success={2}",
                    position.TradeType,
                    profit,
                    result.IsSuccessful
                );
            }

            CheckTotalProfit();

            if (_stopping)
                return;

            // Open a new BUY + SELL pair for the new H1 candle.
            if (!GetBotPositions().Any())
                OpenBuyAndSell();
        }

        private void OpenBuyAndSell()
        {
            Print("Opening BUY + SELL pair...");

            var buyResult = ExecuteMarketOrder(
                TradeType.Buy,
                _symbol.Name,
                _volumeInUnits,
                BotLabel
            );

            if (buyResult.IsSuccessful)
                Print("BUY OPENED. Position ID: {0}", buyResult.Position.Id);
            else
                Print("BUY FAILED: {0}", buyResult.Error);

            var sellResult = ExecuteMarketOrder(
                TradeType.Sell,
                _symbol.Name,
                _volumeInUnits,
                BotLabel
            );

            if (sellResult.IsSuccessful)
                Print("SELL OPENED. Position ID: {0}", sellResult.Position.Id);
            else
                Print("SELL FAILED: {0}", sellResult.Error);
        }

        private void ManagePositions()
        {
            foreach (var position in GetBotPositions().ToArray())
            {
                double profit = position.NetProfit;

                // Close individual trade at +$15.
                if (profit >= ProfitTargetUsd)
                {
                    var result = ClosePosition(position);

                    Print(
                        "{0} reached +${1:F2}. CLOSED. Success={2}",
                        position.TradeType,
                        profit,
                        result.IsSuccessful
                    );

                    continue;
                }

                // Close individual trade at -$5.
                if (profit <= -LossLimitUsd)
                {
                    var result = ClosePosition(position);

                    Print(
                        "{0} reached -${1:F2}. CLOSED. Success={2}",
                        position.TradeType,
                        Math.Abs(profit),
                        result.IsSuccessful
                    );

                    if (result.IsSuccessful)
                        ProtectRemainingTrade();
                }
            }
        }

        private void ProtectRemainingTrade()
        {
            foreach (var position in GetBotPositions())
            {
                if (position.NetProfit >= ProtectedProfitUsd)
                {
                    Print(
                        "Remaining {0} already has +${1:F2} profit.",
                        position.TradeType,
                        position.NetProfit
                    );

                    continue;
                }

                double? protectedPrice =
                    CalculatePriceForProfit(position, ProtectedProfitUsd);

                if (!protectedPrice.HasValue)
                {
                    Print("ERROR: Could not calculate protected-profit price.");
                    continue;
                }

                double stopLoss = protectedPrice.Value;

                if (position.TradeType == TradeType.Buy)
                {
                    stopLoss = Math.Min(
                        stopLoss,
                        _symbol.Bid - (_symbol.PipSize * 0.1)
                    );
                }
                else
                {
                    stopLoss = Math.Max(
                        stopLoss,
                        _symbol.Ask + (_symbol.PipSize * 0.1)
                    );
                }

                var result = ModifyPosition(
                    position,
                    stopLoss,
                    position.TakeProfit
                );

                Print(
                    "Protected remaining {0} at approximately +${1:F2}. SL={2}. Success={3}",
                    position.TradeType,
                    ProtectedProfitUsd,
                    stopLoss,
                    result.IsSuccessful
                );
            }
        }

        private double? CalculatePriceForProfit(
            Position position,
            double desiredProfit)
        {
            // Use actual position profit-per-pip when available.
            if (Math.Abs(position.Pips) > 0.000001)
            {
                double profitPerPip =
                    position.NetProfit / position.Pips;

                if (Math.Abs(profitPerPip) > 0.0000001)
                {
                    double desiredPips =
                        desiredProfit / profitPerPip;

                    if (position.TradeType == TradeType.Buy)
                    {
                        return position.EntryPrice +
                               desiredPips * _symbol.PipSize;
                    }

                    return position.EntryPrice -
                           desiredPips * _symbol.PipSize;
                }
            }

            // Fallback using symbol tick economics.
            if (_symbol.TickValue > 0 &&
                _symbol.TickSize > 0 &&
                position.VolumeInUnits > 0)
            {
                double valuePerPriceUnit =
                    (_symbol.TickValue / _symbol.TickSize) *
                    position.VolumeInUnits;

                if (valuePerPriceUnit > 0)
                {
                    double priceDifference =
                        desiredProfit / valuePerPriceUnit;

                    if (position.TradeType == TradeType.Buy)
                    {
                        return position.EntryPrice +
                               priceDifference;
                    }

                    return position.EntryPrice -
                           priceDifference;
                }
            }

            return null;
        }

        private void CheckTotalProfit()
        {
            if (_sessionProfit < TotalProfitStopUsd)
                return;

            if (_stopping)
                return;

            _stopping = true;

            Print("======================================");
            Print(
                "TOTAL PROFIT TARGET REACHED: +${0:F2}",
                _sessionProfit
            );
            Print("Closing all bot positions.");
            Print("STOPPING BOT.");
            Print("======================================");

            foreach (var position in GetBotPositions().ToArray())
                ClosePosition(position);

            Stop();
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != BotLabel)
                return;

            if (args.Position.SymbolName != _symbol.Name)
                return;

            _sessionProfit += args.Position.NetProfit;

            Print(
                "POSITION CLOSED: {0} | P/L=${1:F2} | SESSION P/L=${2:F2}",
                args.Position.TradeType,
                args.Position.NetProfit,
                _sessionProfit
            );

            CheckTotalProfit();
        }

        private Position[] GetBotPositions()
        {
            return Positions
                .Where(p =>
                    p.Label == BotLabel &&
                    p.SymbolName == _symbol.Name)
                .ToArray();
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;

            Print(
                "GoldH1PairBot STOPPED. Session P/L=${0:F2}",
                _sessionProfit
            );
        }
    }
}