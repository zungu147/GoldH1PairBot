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

        [Parameter("First Profit Trigger (USD)", DefaultValue = 5.0)]
        public double FirstProfitTriggerUsd { get; set; }

        [Parameter("First Protected Profit (USD)", DefaultValue = 3.0)]
        public double FirstProtectedProfitUsd { get; set; }

        [Parameter("Second Profit Trigger (USD)", DefaultValue = 7.0)]
        public double SecondProfitTriggerUsd { get; set; }

        [Parameter("Second Protected Profit (USD)", DefaultValue = 5.0)]
        public double SecondProtectedProfitUsd { get; set; }

        [Parameter("Final Profit Target (USD)", DefaultValue = 16.0)]
        public double FinalProfitTargetUsd { get; set; }

        [Parameter("Bot Label", DefaultValue = "GoldH1PairBot")]
        public string BotLabel { get; set; }

        private Symbol _symbol;
        private double _volumeInUnits;
        private DateTime _currentH1Bar;

        // Protection state for the currently active H1 pair.
        private bool _firstProtectionActivated;
        private bool _secondProtectionActivated;

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
            Print("First Profit Trigger: ${0:F2}", FirstProfitTriggerUsd);
            Print("First Protected Profit: ${0:F2}", FirstProtectedProfitUsd);
            Print("Second Profit Trigger: ${0:F2}", SecondProfitTriggerUsd);
            Print("Second Protected Profit: ${0:F2}", SecondProtectedProfitUsd);
            Print("Final Profit Target: ${0:F2}", FinalProfitTargetUsd);
            Print("Timeframe: H1");
            Print("======================================");

            // If the bot starts while positions from an earlier run exist,
            // manage them rather than immediately opening another pair.
            if (GetBotPositions().Any())
            {
                Print("Existing bot positions detected. They will be managed.");
            }
            else
            {
                Print("No existing positions. Opening initial BUY + SELL pair.");
                OpenNewH1Pair();
            }
        }

        protected override void OnTick()
        {
            CheckForNewH1Candle();

            // Manage the active pair continuously on every tick.
            ManagePositions();
        }

        private void CheckForNewH1Candle()
        {
            DateTime latestBar = Bars.OpenTimes.LastValue;

            if (latestBar <= _currentH1Bar)
                return;

            _currentH1Bar = latestBar;

            Print("--------------------------------------");
            Print("NEW H1 CANDLE: {0}", _currentH1Bar);
            Print("Closing previous H1 candle positions.");
            Print("--------------------------------------");

            // IMPORTANT:
            // Every H1 candle is a completely new trade cycle.
            // Close ALL remaining positions from the previous candle first.
            CloseAllBotPositions();

            // Reset protection state for the new H1 candle.
            ResetProtectionState();

            // Open a fresh BUY + SELL pair.
            OpenNewH1Pair();
        }

        private void OpenNewH1Pair()
        {
            if (GetBotPositions().Any())
            {
                Print("WARNING: Positions still exist. New pair will not be opened.");
                return;
            }

            Print("Opening NEW H1 BUY + SELL pair...");

            var buyResult = ExecuteMarketOrder(
                TradeType.Buy,
                _symbol.Name,
                _volumeInUnits,
                BotLabel
            );

            if (buyResult.IsSuccessful)
            {
                Print(
                    "BUY OPENED | ID={0} | Entry={1}",
                    buyResult.Position.Id,
                    buyResult.Position.EntryPrice
                );
            }
            else
            {
                Print("BUY FAILED: {0}", buyResult.Error);
            }

            var sellResult = ExecuteMarketOrder(
                TradeType.Sell,
                _symbol.Name,
                _volumeInUnits,
                BotLabel
            );

            if (sellResult.IsSuccessful)
            {
                Print(
                    "SELL OPENED | ID={0} | Entry={1}",
                    sellResult.Position.Id,
                    sellResult.Position.EntryPrice
                );
            }
            else
            {
                Print("SELL FAILED: {0}", sellResult.Error);
            }

            Print("New H1 pair opened.");
        }

        private void ManagePositions()
        {
            var positions = GetBotPositions();

            if (!positions.Any())
                return;

            // --------------------------------------------------
            // STEP 1
            // When one trade reaches +$5:
            // - close the opposite trade
            // - protect the winning trade at +$3
            // --------------------------------------------------
            if (!_firstProtectionActivated)
            {
                Position triggerPosition = positions
                    .FirstOrDefault(p => p.NetProfit >= FirstProfitTriggerUsd);

                if (triggerPosition != null)
                {
                    ActivateFirstProtection(triggerPosition);
                    return;
                }
            }

            // --------------------------------------------------
            // STEP 2
            // When the remaining trade reaches +$7:
            // move protection from +$3 to +$5.
            // --------------------------------------------------
            if (_firstProtectionActivated && !_secondProtectionActivated)
            {
                Position remainingPosition = GetBotPositions().FirstOrDefault();

                if (remainingPosition != null &&
                    remainingPosition.NetProfit >= SecondProfitTriggerUsd)
                {
                    ActivateSecondProtection(remainingPosition);
                    return;
                }
            }

            // --------------------------------------------------
            // STEP 3
            // Close remaining trade at +$16.
            // --------------------------------------------------
            foreach (var position in GetBotPositions().ToArray())
            {
                if (position.NetProfit >= FinalProfitTargetUsd)
                {
                    CloseAtFinalTarget(position);
                }
            }
        }

        private void ActivateFirstProtection(Position winningPosition)
        {
            if (_firstProtectionActivated)
                return;

            _firstProtectionActivated = true;

            Print("======================================");
            Print(
                "{0} reached FIRST trigger: +${1:F2}",
                winningPosition.TradeType,
                winningPosition.NetProfit
            );
            Print("Closing opposite trade.");
            Print(
                "Protecting remaining trade at approximately +${0:F2}.",
                FirstProtectedProfitUsd
            );
            Print("======================================");

            // Close every other bot position.
            foreach (var position in GetBotPositions().ToArray())
            {
                if (position.Id == winningPosition.Id)
                    continue;

                double profit = position.NetProfit;

                var result = ClosePosition(position);

                Print(
                    "OPPOSITE {0} CLOSED | P/L=${1:F2} | Success={2}",
                    position.TradeType,
                    profit,
                    result.IsSuccessful
                );
            }

            // The winning position may have moved slightly while
            // the opposite position was being closed, so get it again.
            var remainingPosition = GetBotPositions()
                .FirstOrDefault(p => p.Id == winningPosition.Id);

            if (remainingPosition == null)
            {
                Print("WARNING: Winning position no longer exists.");
                return;
            }

            SetProtection(
                remainingPosition,
                FirstProtectedProfitUsd
            );
        }

        private void ActivateSecondProtection(Position position)
        {
            if (_secondProtectionActivated)
                return;

            _secondProtectionActivated = true;

            Print("======================================");
            Print(
                "{0} reached SECOND trigger: +${1:F2}",
                position.TradeType,
                position.NetProfit
            );
            Print(
                "Moving protection to approximately +${0:F2}.",
                SecondProtectedProfitUsd
            );
            Print("======================================");

            SetProtection(
                position,
                SecondProtectedProfitUsd
            );
        }

        private void SetProtection(
            Position position,
            double protectedProfit)
        {
            double? protectedPrice =
                CalculatePriceForProfit(position, protectedProfit);

            if (!protectedPrice.HasValue)
            {
                Print(
                    "ERROR: Could not calculate protection price for {0}.",
                    position.TradeType
                );

                return;
            }

            double stopLoss = protectedPrice.Value;

            // Make sure the SL remains on the correct executable side
            // of the current market price.
            if (position.TradeType == TradeType.Buy)
            {
                double maximumValidStop =
                    _symbol.Bid - (_symbol.PipSize * 0.1);

                stopLoss = Math.Min(
                    stopLoss,
                    maximumValidStop
                );
            }
            else
            {
                double minimumValidStop =
                    _symbol.Ask + (_symbol.PipSize * 0.1);

                stopLoss = Math.Max(
                    stopLoss,
                    minimumValidStop
                );
            }

            stopLoss = _symbol.NormalizePrice(stopLoss);

            // Never move an existing SL backward.
            if (position.StopLoss.HasValue)
            {
                if (position.TradeType == TradeType.Buy &&
                    stopLoss <= position.StopLoss.Value)
                {
                    Print(
                        "Existing BUY SL is already at or above requested protection. No change."
                    );

                    return;
                }

                if (position.TradeType == TradeType.Sell &&
                    stopLoss >= position.StopLoss.Value)
                {
                    Print(
                        "Existing SELL SL is already at or below requested protection. No change."
                    );

                    return;
                }
            }

            var result = ModifyPosition(
                position,
                stopLoss,
                position.TakeProfit
            );

            Print(
                "{0} PROTECTION UPDATED | Target profit=+${1:F2} | SL={2} | Success={3}",
                position.TradeType,
                protectedProfit,
                stopLoss,
                result.IsSuccessful
            );

            if (!result.IsSuccessful)
            {
                Print(
                    "Protection modification failed: {0}",
                    result.Error
                );
            }
        }

        private double? CalculatePriceForProfit(
            Position position,
            double desiredProfit)
        {
            // Preferred method:
            // Use the position's actual profit-per-pip.
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

            // Fallback:
            // Calculate the monetary value of one price unit.
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

        private void CloseAtFinalTarget(Position position)
        {
            double profit = position.NetProfit;

            Print(
                "{0} reached FINAL TARGET +${1:F2}. Closing position.",
                position.TradeType,
                FinalProfitTargetUsd
            );

            var result = ClosePosition(position);

            Print(
                "FINAL TARGET CLOSE | {0} | Actual P/L=${1:F2} | Success={2}",
                position.TradeType,
                profit,
                result.IsSuccessful
            );
        }

        private void CloseAllBotPositions()
        {
            foreach (var position in GetBotPositions().ToArray())
            {
                double profit = position.NetProfit;

                var result = ClosePosition(position);

                Print(
                    "H1 CANDLE CLOSE | {0} | P/L=${1:F2} | Success={2}",
                    position.TradeType,
                    profit,
                    result.IsSuccessful
                );
            }
        }

        private void ResetProtectionState()
        {
            _firstProtectionActivated = false;
            _secondProtectionActivated = false;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (_symbol == null)
                return;

            if (args.Position.Label != BotLabel)
                return;

            if (args.Position.SymbolName != _symbol.Name)
                return;

            Print(
                "POSITION CLOSED | {0} | P/L=${1:F2}",
                args.Position.TradeType,
                args.Position.NetProfit
            );
        }

        private Position[] GetBotPositions()
        {
            if (_symbol == null)
                return Array.Empty<Position>();

            return Positions
                .Where(p =>
                    p.Label == BotLabel &&
                    p.SymbolName == _symbol.Name)
                .ToArray();
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;

            Print("======================================");
            Print("GoldH1PairBot STOPPED");
            Print("======================================");
        }
    }
}