#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	// =============================================================================
	// TRADEIFY SELECT 25K V2 — MNQ 5M
	//
	// Instalar: copia este archivo a
	//   Documents\NinjaTrader 8\bin\Custom\Strategies\TradeifySelect25kBotV2.cs
	// Compila en NinjaScript Editor (F5). En Strategy Analyzer usa MNQ, 5 min.
	// Primera prueba: deja "Simulate Challenge In Backtest" en FALSE para ver
	// si hay edge. En live/eval deja Phase = Evaluation.
	//
	// Lo que se reescribio vs la version que daba 14 trades / -$40:
	//   - VWAP de RTH (9:30 ET), no el VWAP overnight que deja el sesgo al reves
	//   - Longs y shorts
	//   - Tres setups reales: rebote VWAP, retest del opening range, tranca
	//   - No pide close > VWAP mientras el precio esta haciendo el pullback
	//   - El backtest ya no carga el CSV ni cierra la cuenta al llegar a $1500
	// =============================================================================

	public enum TradeifySelectPhaseV2
	{
		Evaluation,
		FundedDaily
	}

	public class TradeifySelect25kBotV2 : Strategy
	{
		private EMA emaMid;
		private EMA emaSlow;
		private ATR atr;
		private ADX adx;
		private RSI rsi;
		private SMA volumeSma;
		private TimeZoneInfo easternZone;

		private double vwapCumVolume;
		private double vwapCumVolumePrice;
		private double currentVwap;
		private bool rthVwapStarted;

		private double sessionStartCumProfit;
		private double tradeStartCumProfit;
		private double dailyPnL;
		private double challengePnL;
		private double peakEodEquity;

		private int tradesToday;
		private int consecutiveLosses;
		private int pauseUntilBar;
		private int lastSessionResetBar = -1;
		private DateTime lastSessionNyDate = DateTime.MinValue;
		private int orBarsCollected;
		private int barsInTrade;
		private int stopTicksPlanned;
		private int lastSignalDirection;
		private int greenDaysCount;

		private bool dailyLocked;
		private bool challengeLocked;
		private bool exitPending;
		private bool openingRangeReady;
		private bool breakEvenMoved;
		private bool trailingActive;
		private bool stateLoaded;

		private double sessionOrHigh;
		private double sessionOrLow;
		private double entryPrice;
		private double trailingStopPrice;

		private string lastSignalReason = "INIT";
		private string marketBias = "NEUTRAL";
		private string safetyStatus = "OK";

		private readonly List<string> dayKeys = new List<string>();
		private readonly List<double> dayPnls = new List<double>();

		private int diagEligibleBars;
		private int diagAtr;
		private int diagNoTrend;
		private int diagNoSetup;
		private int diagSetupFound;
		private int diagScore;
		private int diagOrRange;
		private int diagAdx;
		private int diagVwapExt;
		private int diagRsi;
		private int diagRisk;
		private int diagEntries;
		private int diagVwapHit;
		private int diagRetestHit;
		private int diagStallHit;
		private int diagOrBreakHit;

		private int armedDirection;
		private int waitBars;
		private int scalpQty;
		private int runQty;
		private int tradeQtyPlanned;
		private int scalpTicksPlanned;
		private int runnerTicksPlanned;
		private double stallHigh;
		private double stallLow;
		private bool waitingForBreak;
		private bool scalpFilled;
		private int tradeDirection;
		private string scalpName = string.Empty;
		private string runName = string.Empty;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "TradeifySelect25kBotV2";
				Description = "V2 MNQ 5m Tradeify Select 25k: VWAP bounce + OR retest, longs y shorts.";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 2;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds = 90;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 2;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 60;
				IsInstantiatedOnEachOptimizationIteration = false;
				IncludeCommission = true;

				Phase = TradeifySelectPhaseV2.Evaluation;
				AccountStartBalance = 25000;
				ChallengeProfitTarget = 1500;
				MaxEodDrawdown = 1000;
				ConsistencyPercent = 40;
				MinTradingDays = 3;
				SimulateChallengeInBacktest = false;

				Contracts = 4;
				MaxContracts = 4;
				MaxTradesPerDay = 5;
				MaxTradesFriday = 3;
				ScalpProfitDollars = 80;
				UseRunnerLeg = true;
				AllowEnterOnStallCandle = true;
				AllowShorts = true;
				TradeWithTrend = true;
				UseVwapBounce = true;
				UseOrRetest = true;
				UseStallPattern = true;
				UseOrBreakout = false;

				ImpulseBodyAtr = 0.22;
				StallRangeRatio = 0.92;
				StallBodyRatio = 0.55;
				StallTimeoutBars = 6;
				RunnerTargetMultiple = 2.2;

				DailyProfitTarget = 300;
				EvalDailyHardCap = 580;
				DailyLossLimit = -200;
				FundedDailyLossLimit = -250;
				StopAfterConsecutiveLosses = 3;
				RiskPerTradeDollars = 80;
				ScalpTargetR = 1.25;
				MinStopTicks = 16;
				MaxStopTicks = 56;
				AtrStopMultiplier = 0.50;
				UseStructureStop = true;
				StructureStopBufferTicks = 2;
				MoveToBreakEvenAtR = 1.25;
				BreakEvenPlusTicks = 2;
				StartTrailAtR = 1.7;
				TrailAtrMultiplier = 1.6;
				MaxBarsInTrade = 24;
				MinBarsInTrade = 1;

				MinSignalScore = 60;
				OpeningRangeBars = 3;
				MidEmaPeriod = 21;
				SlowEmaPeriod = 50;
				AtrPeriod = 14;
				AdxPeriod = 14;
				MinAdx = 12;
				RsiPeriod = 14;
				RsiOverbought = 72;
				RsiOversold = 28;
				VolumePeriod = 20;
				VolumeMinRatio = 0.70;
				MaxExtensionFromVwapTicks = 180;
				MinOrRangePoints = 6;
				MaxOrRangePoints = 90;
				VwapTagTicks = 8;

				TradeStartHour = 9;
				TradeStartMinute = 45;
				MorningEndHour = 11;
				MorningEndMinute = 30;
				AfternoonStartHour = 14;
				AfternoonStartMinute = 0;
				TradeEndHour = 15;
				TradeEndMinute = 30;
				FlattenHour = 15;
				FlattenMinute = 50;
				FridayStopHour = 15;
				FridayStopMinute = 0;

				ForceMNQ = true;
				ForceFiveMinute = true;
				AllowLiveAccounts = false;
				PersistChallengeDays = true;
				DrawPanel = true;
				DrawOrLines = true;
				ShowFunnelDiagnostics = true;
			}
			else if (State == State.Configure)
			{
				try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
				catch
				{
					try { easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
					catch { easternZone = TimeZoneInfo.Local; }
				}
			}
			else if (State == State.DataLoaded)
			{
				emaMid = EMA(MidEmaPeriod);
				emaSlow = EMA(SlowEmaPeriod);
				atr = ATR(AtrPeriod);
				adx = ADX(AdxPeriod);
				rsi = RSI(RsiPeriod, 3);
				volumeSma = SMA(Volume, VolumePeriod);
				peakEodEquity = AccountStartBalance;
				LoadPersistedDays();
				ResetSessionState(true);
			}
			else if (State == State.Terminated)
			{
				SavePersistedDays();
				if (ShowFunnelDiagnostics)
					PrintFunnel();
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade)
				return;

			DateTime ny = ToNy(Time[0]);

			if (Bars.IsFirstBarOfSession && lastSessionResetBar != CurrentBar)
			{
				if (lastSessionResetBar >= 0 && lastSessionNyDate != DateTime.MinValue)
				{
					double closedDay = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
					StoreToday(lastSessionNyDate, closedDay);
				}
				ResetSessionState(false);
			}

			UpdateRthVwap(ny);
			UpdateOpeningRange(ny);
			UpdateDailyPnL();
			UpdateMarketBias();
			EnforceAccountGuards();

			if (DrawPanel)
				DrawStatusPanel(ny);

			if (!PassesInstrumentChecks())
				return;

			if (IsFlattenTime(ny))
			{
				FlattenIfNeeded("FlattenET");
				safetyStatus = "FLAT FIN DE DIA";
				if (Position.MarketPosition == MarketPosition.Flat)
					SavePersistedDays();
				return;
			}

			if (challengeLocked || dailyLocked)
			{
				FlattenIfNeeded("Locked");
				return;
			}

			if (Position.MarketPosition != MarketPosition.Flat)
			{
				barsInTrade++;
				ManageOpenTrade();
				return;
			}

			exitPending = false;
			barsInTrade = 0;

			if (!IsTradingWindowOpen(ny))
				return;

			if (!CanTradeAccount())
				return;

			int maxTrades = IsFriday(ny) ? MaxTradesFriday : MaxTradesPerDay;
			if (tradesToday >= maxTrades)
			{
				safetyStatus = "MAX TRADES HOY";
				return;
			}

			if (CurrentBar <= pauseUntilBar)
			{
				safetyStatus = "PAUSA COOLDOWN";
				return;
			}

			if (!openingRangeReady)
			{
				safetyStatus = "ARMANDO OPENING RANGE";
				return;
			}

			SignalSnapshot signal = BuildSignal(ny);
			lastSignalDirection = signal.Direction;
			lastSignalReason = signal.Reason;

			if (signal.Direction == 0)
				return;

			diagSetupFound++;

			if (signal.Score < MinSignalScore)
			{
				diagScore++;
				safetyStatus = "SETUP FLOJO (" + signal.Score + "<" + MinSignalScore + ")";
				return;
			}

			if (!PassesEntryFilters(signal))
				return;

			if (!ValidateRiskGeometry(signal.Direction))
			{
				diagRisk++;
				return;
			}

			diagEntries++;
			SubmitTrade(signal);
		}

		private bool PassesEntryFilters(SignalSnapshot signal)
		{
			if (!IsOpeningRangeValid())
			{
				diagOrRange++;
				safetyStatus = "RANGO APERTURA FUERA DE RANGO";
				return false;
			}

			bool bounce = signal.Reason.IndexOf("VWAP", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!bounce && adx[0] < MinAdx)
			{
				diagAdx++;
				safetyStatus = "ADX FLOJO — SIN DIRECCION";
				return false;
			}

			if (!bounce
				&& currentVwap > 0
				&& Math.Abs(Close[0] - currentVwap) / TickSize > MaxExtensionFromVwapTicks)
			{
				diagVwapExt++;
				safetyStatus = "MUY LEJOS DEL VWAP — NO PERSIGO";
				return false;
			}

			if (signal.Direction > 0 && rsi[0] >= RsiOverbought)
			{
				diagRsi++;
				safetyStatus = "RSI SOBRECOMPRADO";
				return false;
			}

			if (signal.Direction < 0 && rsi[0] <= RsiOversold)
			{
				diagRsi++;
				safetyStatus = "RSI SOBREVENDIDO";
				return false;
			}

			return true;
		}

		private void PrintFunnel()
		{
			Print("===== EMBUDO TradeifySelect25kBotV2 =====");
			Print("Velas evaluables (flat, en horario, OR lista) : " + diagEligibleBars);
			Print("  descartadas por ATR fuera de rango          : " + diagAtr);
			Print("  descartadas por falta de sesgo              : " + diagNoTrend);
			Print("  sesgo OK pero sin setup                     : " + diagNoSetup);
			Print("Setups encontrados                            : " + diagSetupFound);
			Print("  VWAP bounce                                 : " + diagVwapHit);
			Print("  OR retest                                   : " + diagRetestHit);
			Print("  stall / absorcion                           : " + diagStallHit);
			Print("  OR breakout                                 : " + diagOrBreakHit);
			Print("  descartados por Min Signal Score            : " + diagScore);
			Print("  descartados por rango de apertura           : " + diagOrRange);
			Print("  descartados por ADX bajo                    : " + diagAdx);
			Print("  descartados por lejania del VWAP            : " + diagVwapExt);
			Print("  descartados por RSI extremo                 : " + diagRsi);
			Print("  descartados por geometria de riesgo         : " + diagRisk);
			Print("ENTRADAS ENVIADAS                             : " + diagEntries);
			Print("=======================================");
		}

		private bool ChallengeLocksActive()
		{
			if (State == State.Historical && !SimulateChallengeInBacktest)
				return false;
			return true;
		}

		private void EnforceAccountGuards()
		{
			double equity = AccountStartBalance + SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
			if (Position.MarketPosition != MarketPosition.Flat)
				equity += Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);

			double floor = peakEodEquity - MaxEodDrawdown;
			if (peakEodEquity >= AccountStartBalance + MaxEodDrawdown + 100)
				floor = AccountStartBalance + 100;

			if (equity <= floor + 80)
			{
				challengeLocked = true;
				safetyStatus = "STOP DRAWDOWN EOD";
				FlattenIfNeeded("EodDd");
				return;
			}

			if (Phase == TradeifySelectPhaseV2.Evaluation)
			{
				if (ChallengeLocksActive()
					&& challengePnL >= ChallengeProfitTarget
					&& greenDaysCount >= MinTradingDays
					&& ConsistencyOk(true))
				{
					challengeLocked = true;
					safetyStatus = "CHALLENGE COMPLETADO — PARA";
					FlattenIfNeeded("ChallengeDone");
					return;
				}

				if (ChallengeLocksActive() && dailyPnL >= Math.Min(DailyProfitTarget, EvalDailyHardCap))
				{
					LockDay("META DIARIA");
					FlattenIfNeeded("DailyTarget");
					return;
				}

				if (dailyPnL <= DailyLossLimit)
				{
					LockDay("PERDIDA DIARIA");
					FlattenIfNeeded("DailyLoss");
					return;
				}
			}
			else
			{
				if (ChallengeLocksActive() && dailyPnL >= DailyProfitTarget)
				{
					LockDay("META FONDEADA");
					FlattenIfNeeded("DailyTarget");
					return;
				}

				if (dailyPnL <= FundedDailyLossLimit)
				{
					LockDay("DLL FONDEADA");
					FlattenIfNeeded("DailyLoss");
					return;
				}
			}

			if (consecutiveLosses >= StopAfterConsecutiveLosses)
			{
				LockDay("RACHA PERDIDAS");
				FlattenIfNeeded("LossStreak");
			}
		}

		private SignalSnapshot BuildSignal(DateTime ny)
		{
			SignalSnapshot s = None("LEYENDO VELAS");

			if (CurrentBar < 12)
				return None("POCAS VELAS");

			diagEligibleBars++;

			double atrTicks = atr[0] / TickSize;
			if (atrTicks < 12 || atrTicks > 200)
			{
				diagAtr++;
				return None("ATR MUERTO O LOCO");
			}

			if (waitingForBreak && armedDirection != 0)
			{
				s = ResolveArmedStall();
				if (s.Direction != 0 || waitingForBreak)
					return s;
			}

			bool longBias = IsLongBias();
			bool shortBias = AllowShorts && IsShortBias();

			if (UseVwapBounce)
			{
				if (longBias)
				{
					s = TryVwapBounce(1);
					if (s.Direction != 0)
					{
						diagVwapHit++;
						return s;
					}
				}

				if (shortBias)
				{
					s = TryVwapBounce(-1);
					if (s.Direction != 0)
					{
						diagVwapHit++;
						return s;
					}
				}
			}

			if (UseOrRetest && IsMorning(ny))
			{
				if (longBias)
				{
					s = TryOrRetest(1);
					if (s.Direction != 0)
					{
						diagRetestHit++;
						return s;
					}
				}

				if (shortBias)
				{
					s = TryOrRetest(-1);
					if (s.Direction != 0)
					{
						diagRetestHit++;
						return s;
					}
				}
			}

			if (UseOrBreakout && IsMorning(ny) && !IsChop(8))
			{
				if (longBias)
				{
					s = TryOrBreakout(1);
					if (s.Direction != 0)
					{
						diagOrBreakHit++;
						return s;
					}
				}

				if (shortBias)
				{
					s = TryOrBreakout(-1);
					if (s.Direction != 0)
					{
						diagOrBreakHit++;
						return s;
					}
				}
			}

			if (UseStallPattern)
			{
				if (longBias)
				{
					s = TryStall(1);
					if (s.Direction != 0 || waitingForBreak)
					{
						if (s.Direction != 0)
							diagStallHit++;
						return s;
					}
				}

				if (shortBias)
				{
					s = TryStall(-1);
					if (s.Direction != 0 || waitingForBreak)
					{
						if (s.Direction != 0)
							diagStallHit++;
						return s;
					}
				}
			}

			if (!longBias && !shortBias)
			{
				diagNoTrend++;
				return None(AllowShorts ? "SIN SESGO CLARO" : "SIN SESGO ALCISTA");
			}

			diagNoSetup++;
			return None("SESGO OK — ESPERANDO SETUP");
		}

		private SignalSnapshot None(string reason)
		{
			safetyStatus = reason;
			return new SignalSnapshot { Direction = 0, Score = 0, Reason = reason };
		}

		private SignalSnapshot Hit(int direction, int score, string reason)
		{
			safetyStatus = reason;
			return new SignalSnapshot { Direction = direction, Score = score, Reason = reason };
		}

		// Sesgo de FONDO. El pullback puede cerrar debajo de la EMA21 / VWAP;
		// pedir close > VWAP en el mismo bar del toque era lo que mataba las entradas.
		private bool IsLongBias()
		{
			if (emaMid[0] < emaSlow[0])
				return false;
			if (Close[0] < emaSlow[0] && Close[1] < emaSlow[1])
				return false;
			if (!TradeWithTrend)
				return true;
			return currentVwap <= 0 || HighestHigh(4) >= currentVwap;
		}

		private bool IsShortBias()
		{
			if (emaMid[0] > emaSlow[0])
				return false;
			if (Close[0] > emaSlow[0] && Close[1] > emaSlow[1])
				return false;
			if (!TradeWithTrend)
				return true;
			return currentVwap <= 0 || LowestLow(4) <= currentVwap;
		}

		private SignalSnapshot TryVwapBounce(int direction)
		{
			if (currentVwap <= 0)
				return None("VWAP NO LISTO");

			double range = High[0] - Low[0];
			if (range < 6 * TickSize || IsExhaustionBar())
				return None("VELA INVALIDA VWAP");

			double tag = Math.Max(VwapTagTicks * TickSize, atr[0] * 0.25);
			double maxOvershoot = Math.Max(atr[0] * 0.55, 10 * TickSize);

			if (direction > 0)
			{
				bool tagged = Low[0] <= currentVwap + tag && Low[0] >= currentVwap - maxOvershoot;
				bool reclaim = Close[0] > currentVwap
					&& Close[0] > Open[0]
					&& Close[0] >= Low[0] + 0.55 * range;
				if (!tagged || !reclaim)
					return None("SIN REBOTE VWAP LONG");
				return Hit(1, 82, "REBOTE VWAP — LONG");
			}

			bool taggedShort = High[0] >= currentVwap - tag && High[0] <= currentVwap + maxOvershoot;
			bool reject = Close[0] < currentVwap
				&& Close[0] < Open[0]
				&& Close[0] <= High[0] - 0.55 * range;
			if (!taggedShort || !reject)
				return None("SIN REBOTE VWAP SHORT");
			return Hit(-1, 82, "REBOTE VWAP — SHORT");
		}

		private SignalSnapshot TryOrRetest(int direction)
		{
			if (sessionOrHigh <= sessionOrLow)
				return None("OR INVALIDO");

			double range = High[0] - Low[0];
			if (range < 6 * TickSize || IsExhaustionBar())
				return None("VELA INVALIDA RETEST");

			double buf = 6 * TickSize;
			double maxOvershoot = Math.Max(atr[0] * 0.40, 8 * TickSize);

			if (direction > 0)
			{
				if (!BrokeLevel(sessionOrHigh, 1, 12, true))
					return None("OR HIGH AUN NO ROTO");
				bool tag = Low[0] <= sessionOrHigh + buf && Low[0] >= sessionOrHigh - maxOvershoot;
				bool hold = Close[0] > sessionOrHigh && Close[0] > Open[0];
				if (!tag || !hold)
					return None("SIN RETEST OR HIGH");
				return Hit(1, 86, "RETEST OPENING RANGE — LONG");
			}

			if (!BrokeLevel(sessionOrLow, 1, 12, false))
				return None("OR LOW AUN NO ROTO");
			bool tagLow = High[0] >= sessionOrLow - buf && High[0] <= sessionOrLow + maxOvershoot;
			bool holdShort = Close[0] < sessionOrLow && Close[0] < Open[0];
			if (!tagLow || !holdShort)
				return None("SIN RETEST OR LOW");
			return Hit(-1, 86, "RETEST OPENING RANGE — SHORT");
		}

		private SignalSnapshot TryOrBreakout(int direction)
		{
			if (sessionOrHigh <= sessionOrLow || IsExhaustionBar())
				return None("OR BREAK INVALIDO");

			if (direction > 0)
			{
				if (Close[1] > sessionOrHigh || Close[0] <= sessionOrHigh || Close[0] <= Open[0])
					return None("SIN ROTURA OR HIGH");
				if (volumeSma[0] > 0 && Volume[0] < volumeSma[0] * VolumeMinRatio)
					return None("ROTURA SIN VOLUMEN");
				return Hit(1, 72, "ROTURA OPENING RANGE — LONG");
			}

			if (Close[1] < sessionOrLow || Close[0] >= sessionOrLow || Close[0] >= Open[0])
				return None("SIN ROTURA OR LOW");
			if (volumeSma[0] > 0 && Volume[0] < volumeSma[0] * VolumeMinRatio)
				return None("ROTURA SIN VOLUMEN");
			return Hit(-1, 72, "ROTURA OPENING RANGE — SHORT");
		}

		private SignalSnapshot TryStall(int direction)
		{
			if (direction > 0)
			{
				if (IsAbsorptionLong())
				{
					stallHigh = High[0];
					stallLow = Low[0];
					armedDirection = 1;
					waitingForBreak = false;
					return Hit(1, 78, "ABSORCION — LONG");
				}

				if (IsDropThenStall())
				{
					stallHigh = Math.Max(High[0], High[1]);
					stallLow = Math.Min(Low[0], Low[1]);
					armedDirection = 1;
					waitBars = 0;

					if (AllowEnterOnStallCandle && Close[0] >= Open[0] && Close[0] > emaMid[0])
					{
						waitingForBreak = false;
						return Hit(1, 76, "CAIDA + TRANCA — LONG");
					}

					waitingForBreak = true;
					return None("CAIDA TRANCADA — ESPERANDO ROMPER ARRIBA");
				}
			}
			else
			{
				if (IsAbsorptionShort())
				{
					stallHigh = High[0];
					stallLow = Low[0];
					armedDirection = -1;
					waitingForBreak = false;
					return Hit(-1, 78, "ABSORCION — SHORT");
				}

				if (IsRallyThenStall())
				{
					stallHigh = Math.Max(High[0], High[1]);
					stallLow = Math.Min(Low[0], Low[1]);
					armedDirection = -1;
					waitBars = 0;

					if (AllowEnterOnStallCandle && Close[0] <= Open[0] && Close[0] < emaMid[0])
					{
						waitingForBreak = false;
						return Hit(-1, 76, "SUBIDA + TRANCA — SHORT");
					}

					waitingForBreak = true;
					return None("SUBIDA TRANCADA — ESPERANDO ROMPER ABAJO");
				}
			}

			return None("SIN TRANCA");
		}

		private SignalSnapshot ResolveArmedStall()
		{
			waitBars++;
			if (waitBars > StallTimeoutBars)
			{
				ClearStall();
				return None("TRANCA EXPIRO");
			}

			if (armedDirection > 0)
			{
				if (Close[0] < stallLow - 4 * TickSize)
				{
					ClearStall();
					return None("VENDEDORES SIGUIERON — CANCELADO");
				}

				if (Close[0] > stallHigh && Close[0] > Open[0] && !IsExhaustionBar())
					return Hit(1, 84, "ROMPIO TRANCA — LONG");
			}
			else if (AllowShorts)
			{
				if (Close[0] > stallHigh + 4 * TickSize)
				{
					ClearStall();
					return None("COMPRADORES SIGUIERON — CANCELADO");
				}

				if (Close[0] < stallLow && Close[0] < Open[0] && !IsExhaustionBar())
					return Hit(-1, 84, "ROMPIO TRANCA — SHORT");
			}

			return None("VELA TRANCADA — ESPERANDO");
		}

		private bool IsAbsorptionLong()
		{
			double range = High[0] - Low[0];
			if (range < 8 * TickSize || range > atr[0] * 1.8)
				return false;
			if (Close[0] < emaMid[0])
				return false;

			double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];
			double body = Math.Abs(Close[0] - Open[0]);
			bool redOrTest = Close[0] <= Open[0] || Low[0] < Low[1];
			bool stuck = lowerWick >= range * 0.35 || (body / range <= 0.35 && Close[0] >= Low[0] + 0.55 * range);
			bool closedUp = Close[0] >= Low[0] + 0.55 * range;
			return redOrTest && stuck && closedUp;
		}

		private bool IsAbsorptionShort()
		{
			double range = High[0] - Low[0];
			if (range < 8 * TickSize || range > atr[0] * 1.8)
				return false;
			if (Close[0] > emaMid[0])
				return false;
			double upperWick = High[0] - Math.Max(Open[0], Close[0]);
			bool greenOrTest = Close[0] >= Open[0] || High[0] > High[1];
			bool closedDown = Close[0] <= High[0] - 0.55 * range;
			return greenOrTest && upperWick >= range * 0.35 && closedDown;
		}

		private bool IsDropThenStall()
		{
			if (Close[1] >= Open[1])
				return false;

			double dropRange = High[1] - Low[1];
			double stallRange = High[0] - Low[0];
			double dropBody = Open[1] - Close[1];
			if (dropRange < 8 * TickSize || dropBody < atr[1] * ImpulseBodyAtr)
				return false;

			if (volumeSma[1] > 0 && Volume[1] < volumeSma[1] * VolumeMinRatio)
				return false;

			bool smaller = stallRange <= dropRange * StallRangeRatio;
			bool noFollowThrough = Low[0] >= Low[1] - 4 * TickSize;
			double stallBody = Math.Abs(Close[0] - Open[0]);
			bool tight = stallRange <= 6 * TickSize || (stallRange > 0 && stallBody / stallRange <= StallBodyRatio);
			return smaller && noFollowThrough && tight;
		}

		private bool IsRallyThenStall()
		{
			if (Close[1] <= Open[1])
				return false;

			double rallyRange = High[1] - Low[1];
			double stallRange = High[0] - Low[0];
			double rallyBody = Close[1] - Open[1];
			if (rallyRange < 8 * TickSize || rallyBody < atr[1] * ImpulseBodyAtr)
				return false;

			if (volumeSma[1] > 0 && Volume[1] < volumeSma[1] * VolumeMinRatio)
				return false;

			bool smaller = stallRange <= rallyRange * StallRangeRatio;
			bool noFollowThrough = High[0] <= High[1] + 4 * TickSize;
			double stallBody = Math.Abs(Close[0] - Open[0]);
			bool tight = stallRange <= 6 * TickSize || (stallRange > 0 && stallBody / stallRange <= StallBodyRatio);
			return smaller && noFollowThrough && tight;
		}

		private bool IsExhaustionBar()
		{
			return (High[0] - Low[0]) > atr[0] * 1.65;
		}

		private bool IsChop(int lookback)
		{
			double hh = HighestHigh(lookback);
			double ll = LowestLow(lookback);
			return (hh - ll) < atr[0] * 1.15;
		}

		private bool BrokeLevel(double level, int startBar, int lookback, bool upside)
		{
			int last = Math.Min(lookback, CurrentBar);
			for (int i = startBar; i <= last; i++)
			{
				if (upside && High[i] > level + 2 * TickSize)
					return true;
				if (!upside && Low[i] < level - 2 * TickSize)
					return true;
			}
			return false;
		}

		private double HighestHigh(int lookback)
		{
			double h = High[0];
			int last = Math.Min(lookback - 1, CurrentBar);
			for (int i = 1; i <= last; i++)
				h = Math.Max(h, High[i]);
			return h;
		}

		private double LowestLow(int lookback)
		{
			double l = Low[0];
			int last = Math.Min(lookback - 1, CurrentBar);
			for (int i = 1; i <= last; i++)
				l = Math.Min(l, Low[i]);
			return l;
		}

		private void ClearStall()
		{
			waitingForBreak = false;
			armedDirection = 0;
			waitBars = 0;
		}

		private bool ValidateRiskGeometry(int direction)
		{
			double tickVal = GetTickValue();
			if (tickVal <= 0)
				return false;

			stopTicksPlanned = CalculateStopTicks(direction);
			if (stopTicksPlanned <= 0)
				return false;

			double riskPerContract = stopTicksPlanned * tickVal;
			if (riskPerContract <= 0)
				return false;

			int qty = Math.Min(GetBaseQty(), (int)Math.Floor((RiskPerTradeDollars + 0.01) / riskPerContract));
			if (qty < 1)
			{
				safetyStatus = "STOP MUY ANCHO PARA EL RIESGO — NO";
				return false;
			}

			double lossLimit = Phase == TradeifySelectPhaseV2.Evaluation ? DailyLossLimit : FundedDailyLossLimit;
			while (qty > 1 && dailyPnL - riskPerContract * qty < lossLimit)
				qty--;

			if (dailyPnL - riskPerContract * qty < lossLimit)
			{
				safetyStatus = "EL STOP ROMPERIA EL LIMITE DIARIO";
				return false;
			}

			tradeQtyPlanned = qty;
			scalpTicksPlanned = CalculateScalpTicks(tickVal);
			runnerTicksPlanned = Math.Max(
				(int)Math.Round(stopTicksPlanned * RunnerTargetMultiple),
				scalpTicksPlanned + 8);

			return true;
		}

		private int CalculateScalpTicks(double tickVal)
		{
			int ticks = Math.Max((int)Math.Round(stopTicksPlanned * ScalpTargetR), stopTicksPlanned + 4);
			if (tickVal > 0)
			{
				int capByDollars = (int)Math.Round(ScalpProfitDollars / tickVal);
				if (capByDollars > 8 && ticks > capByDollars)
					ticks = capByDollars;
			}
			return Math.Max(8, ticks);
		}

		private int GetBaseQty()
		{
			int qty = Contracts;
			if (qty > MaxContracts)
				qty = MaxContracts;
			if (qty > 10)
				qty = 10;
			if (dailyPnL <= DailyLossLimit * 0.45)
				qty = 1;
			if (Phase == TradeifySelectPhaseV2.Evaluation && ChallengeLocksActive())
			{
				double remaining = ChallengeProfitTarget - challengePnL;
				if (remaining > 0 && remaining < 350)
					qty = 1;
			}
			return Math.Max(1, qty);
		}

		private int CalculateStopTicks(int direction)
		{
			int atrStop = (int)Math.Round((atr[0] / TickSize) * AtrStopMultiplier);
			int stopTicks = Math.Max(atrStop, MinStopTicks);

			if (UseStructureStop)
			{
				double structPrice = direction > 0
					? Math.Min(Low[0], Low[1]) - StructureStopBufferTicks * TickSize
					: Math.Max(High[0], High[1]) + StructureStopBufferTicks * TickSize;

				int structTicks = direction > 0
					? (int)Math.Ceiling((Close[0] - structPrice) / TickSize)
					: (int)Math.Ceiling((structPrice - Close[0]) / TickSize);

				if (structTicks >= MinStopTicks)
					stopTicks = Math.Max(structTicks, atrStop);
				else if (structTicks > 0)
					stopTicks = Math.Max(stopTicks, MinStopTicks);
			}

			return Clamp(stopTicks, MinStopTicks, MaxStopTicks);
		}

		private void SubmitTrade(SignalSnapshot signal)
		{
			int qty = tradeQtyPlanned > 0 ? tradeQtyPlanned : GetBaseQty();
			bool wantRunner = UseRunnerLeg && qty >= 2;
			scalpQty = wantRunner ? qty - 1 : qty;
			runQty = wantRunner ? 1 : 0;

			string tag = CurrentBar.ToString();
			scalpName = (signal.Direction > 0 ? "TF25V2_S_L_" : "TF25V2_S_S_") + tag;
			runName = (signal.Direction > 0 ? "TF25V2_R_L_" : "TF25V2_R_S_") + tag;

			SetStopLoss(scalpName, CalculationMode.Ticks, stopTicksPlanned, false);
			SetProfitTarget(scalpName, CalculationMode.Ticks, scalpTicksPlanned);

			if (runQty > 0)
			{
				SetStopLoss(runName, CalculationMode.Ticks, stopTicksPlanned, false);
				SetProfitTarget(runName, CalculationMode.Ticks, runnerTicksPlanned);
			}

			entryPrice = 0;
			breakEvenMoved = false;
			trailingActive = false;
			scalpFilled = false;
			trailingStopPrice = 0;
			tradeDirection = signal.Direction;
			tradeStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

			if (signal.Direction > 0)
			{
				EnterLong(scalpQty, scalpName);
				if (runQty > 0)
					EnterLong(runQty, runName);
			}
			else
			{
				EnterShort(scalpQty, scalpName);
				if (runQty > 0)
					EnterShort(runQty, runName);
			}

			ClearStall();
			tradesToday++;
			pauseUntilBar = CurrentBar + 2;

			double tickVal = GetTickValue();
			safetyStatus = "RIESGO $" + (stopTicksPlanned * tickVal * qty).ToString("0")
				+ " | T1 " + scalpTicksPlanned + "t x" + scalpQty
				+ (runQty > 0 ? " | RUNNER " + runnerTicksPlanned + "t" : " | SIN RUNNER");
		}

		private void ManageOpenTrade()
		{
			if (entryPrice <= 0)
				entryPrice = Position.AveragePrice;
			if (entryPrice <= 0 || tradeDirection == 0 || stopTicksPlanned <= 0)
				return;

			int barLimit = scalpFilled && runQty > 0 ? MaxBarsInTrade * 2 : MaxBarsInTrade;
			if (MaxBarsInTrade > 0 && barsInTrade >= Math.Max(MinBarsInTrade, barLimit))
			{
				FlattenIfNeeded(scalpFilled ? "RunnerTime" : "NoLlego");
				return;
			}

			if (barsInTrade < MinBarsInTrade)
				return;

			double profitTicks = tradeDirection > 0
				? (Close[0] - entryPrice) / TickSize
				: (entryPrice - Close[0]) / TickSize;

			if (!breakEvenMoved && profitTicks >= stopTicksPlanned * MoveToBreakEvenAtR)
				MoveBothLegsToBreakEven();

			if (runQty <= 0 || string.IsNullOrEmpty(runName))
				return;

			if (profitTicks < stopTicksPlanned * StartTrailAtR)
				return;

			int trailTicks = Clamp(
				(int)Math.Round((atr[0] / TickSize) * TrailAtrMultiplier),
				Math.Max(8, stopTicksPlanned / 2),
				MaxStopTicks * 2);

			double newStop = tradeDirection > 0
				? Close[0] - trailTicks * TickSize
				: Close[0] + trailTicks * TickSize;

			if (TightenRunnerStop(newStop))
				trailingActive = true;
		}

		private void MoveBothLegsToBreakEven()
		{
			double be = RoundTick(tradeDirection > 0
				? entryPrice + BreakEvenPlusTicks * TickSize
				: entryPrice - BreakEvenPlusTicks * TickSize);

			bool usable = tradeDirection > 0 ? be < Close[0] : be > Close[0];
			if (!usable)
				return;

			if (!scalpFilled && !string.IsNullOrEmpty(scalpName))
				SetStopLoss(scalpName, CalculationMode.Price, be, false);

			if (runQty > 0 && !string.IsNullOrEmpty(runName))
				SetStopLoss(runName, CalculationMode.Price, be, false);

			trailingStopPrice = be;
			breakEvenMoved = true;
		}

		private bool TightenRunnerStop(double price)
		{
			if (string.IsNullOrEmpty(runName) || price <= 0 || tradeDirection == 0)
				return false;

			double p = RoundTick(price);

			if (tradeDirection > 0)
			{
				if (p >= Close[0] || (trailingStopPrice > 0 && p <= trailingStopPrice))
					return false;
			}
			else
			{
				if (p <= Close[0] || (trailingStopPrice > 0 && p >= trailingStopPrice))
					return false;
			}

			trailingStopPrice = p;
			SetStopLoss(runName, CalculationMode.Price, p, false);
			return true;
		}

		private double RoundTick(double price)
		{
			if (Instrument == null || Instrument.MasterInstrument == null)
				return price;
			return Instrument.MasterInstrument.RoundToTickSize(price);
		}

		// VWAP de RTH (9:30-16:00 ET). El VWAP de sesion completa incluye overnight
		// y en MNQ deja el "sesgo" al reves casi todos los dias.
		private void UpdateRthVwap(DateTime ny)
		{
			TimeSpan t = ny.TimeOfDay;
			TimeSpan rthOpen = new TimeSpan(9, 30, 0);
			TimeSpan rthClose = new TimeSpan(16, 0, 0);

			if (t < rthOpen || t >= rthClose)
				return;

			bool firstRth = !rthVwapStarted;
			if (CurrentBar > 0)
			{
				DateTime prev = ToNy(Time[1]);
				if (prev.Date != ny.Date || prev.TimeOfDay < rthOpen)
					firstRth = true;
			}

			if (firstRth)
			{
				vwapCumVolume = 0;
				vwapCumVolumePrice = 0;
				rthVwapStarted = true;
			}

			double typical = (High[0] + Low[0] + Close[0]) / 3.0;
			vwapCumVolume += Volume[0];
			vwapCumVolumePrice += typical * Volume[0];
			if (vwapCumVolume > 0)
				currentVwap = vwapCumVolumePrice / vwapCumVolume;
		}

		private void UpdateOpeningRange(DateTime ny)
		{
			TimeSpan t = ny.TimeOfDay;
			TimeSpan orStart = new TimeSpan(9, 30, 0);
			TimeSpan orEnd = new TimeSpan(9, 45, 0);

			if (t >= orStart && t <= orEnd)
			{
				if (orBarsCollected == 0)
				{
					sessionOrHigh = High[0];
					sessionOrLow = Low[0];
				}
				else
				{
					sessionOrHigh = Math.Max(sessionOrHigh, High[0]);
					sessionOrLow = Math.Min(sessionOrLow, Low[0]);
				}

				orBarsCollected++;
				if (orBarsCollected >= OpeningRangeBars || t >= orEnd)
					openingRangeReady = true;
			}
			else if (t > orEnd && sessionOrHigh > 0 && sessionOrLow > 0)
			{
				openingRangeReady = true;
			}

			if (DrawOrLines && openingRangeReady && ChartControl != null)
			{
				Draw.HorizontalLine(this, "TF25V2_ORH", sessionOrHigh, Brushes.DodgerBlue);
				Draw.HorizontalLine(this, "TF25V2_ORL", sessionOrLow, Brushes.OrangeRed);
				if (currentVwap > 0)
					Draw.HorizontalLine(this, "TF25V2_VWAP", currentVwap, Brushes.Goldenrod);
			}
		}

		private bool IsOpeningRangeValid()
		{
			if (sessionOrHigh <= 0 || sessionOrLow <= 0 || sessionOrHigh <= sessionOrLow)
				return false;
			double pts = sessionOrHigh - sessionOrLow;
			return pts >= MinOrRangePoints && pts <= MaxOrRangePoints;
		}

		private void UpdateMarketBias()
		{
			if (Close[0] > currentVwap && emaMid[0] > emaSlow[0])
				marketBias = "ALCISTA";
			else if (Close[0] < currentVwap && emaMid[0] < emaSlow[0])
				marketBias = "BAJISTA";
			else
				marketBias = "NEUTRAL";
		}

		private void UpdateDailyPnL()
		{
			dailyPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - sessionStartCumProfit;
			if (Position.MarketPosition != MarketPosition.Flat)
				dailyPnL += Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);

			challengePnL = SumStoredExceptToday() + dailyPnL;
			greenDaysCount = CountGreenDays(dailyPnL);
		}

		private void ResetSessionState(bool initial)
		{
			lastSessionResetBar = CurrentBar;
			lastSessionNyDate = ToNy(Time[0]).Date;
			sessionStartCumProfit = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
			dailyPnL = 0;
			tradesToday = 0;
			consecutiveLosses = 0;
			pauseUntilBar = -1;
			dailyLocked = false;
			safetyStatus = "OK";
			vwapCumVolume = 0;
			vwapCumVolumePrice = 0;
			currentVwap = Close[0];
			rthVwapStarted = false;
			sessionOrHigh = 0;
			sessionOrLow = 0;
			orBarsCollected = 0;
			openingRangeReady = false;
			ClearStall();

			double closedEquity = AccountStartBalance + sessionStartCumProfit;
			if (closedEquity > peakEodEquity)
				peakEodEquity = closedEquity;

			if (!initial)
				ResetTradeTracking();
		}

		private void StoreToday(DateTime date, double pnl)
		{
			string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			int idx = dayKeys.IndexOf(key);
			if (idx >= 0)
				dayPnls[idx] = pnl;
			else if (Math.Abs(pnl) > 0.01 || tradesToday > 0)
			{
				dayKeys.Add(key);
				dayPnls.Add(pnl);
			}
			SavePersistedDays();
		}

		private double SumStoredExceptToday()
		{
			string today = ToNy(Time[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			double sum = 0;
			for (int i = 0; i < dayPnls.Count; i++)
			{
				if (dayKeys[i] == today)
					continue;
				sum += dayPnls[i];
			}
			return sum;
		}

		private int CountGreenDays(double todayPnl)
		{
			int n = 0;
			string today = ToNy(Time[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			for (int i = 0; i < dayPnls.Count; i++)
			{
				if (dayKeys[i] == today)
					continue;
				if (dayPnls[i] >= 100)
					n++;
			}
			if (todayPnl >= 100)
				n++;
			return n;
		}

		private bool ConsistencyOk(bool assumeClosedToday)
		{
			double best = assumeClosedToday ? dailyPnL : 0;
			double total = assumeClosedToday ? challengePnL : SumStoredExceptToday();
			string today = ToNy(Time[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

			for (int i = 0; i < dayPnls.Count; i++)
			{
				if (dayKeys[i] == today)
					continue;
				if (dayPnls[i] > best)
					best = dayPnls[i];
			}

			if (total <= 0)
				return false;
			return (best / total) <= (ConsistencyPercent / 100.0) + 0.001;
		}

		private void LockDay(string reason)
		{
			dailyLocked = true;
			safetyStatus = reason;
		}

		private void FlattenIfNeeded(string tag)
		{
			if (exitPending || Position.MarketPosition == MarketPosition.Flat)
				return;
			exitPending = true;
			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong();
			else
				ExitShort();
			safetyStatus = "FLAT " + tag;
		}

		private bool IsTradingWindowOpen(DateTime ny)
		{
			int t = ny.Hour * 10000 + ny.Minute * 100;
			if (IsFriday(ny))
			{
				int friEnd = FridayStopHour * 10000 + FridayStopMinute * 100;
				if (t > friEnd)
				{
					safetyStatus = "VIERNES TEMPRANO";
					return false;
				}
			}

			int start = TradeStartHour * 10000 + TradeStartMinute * 100;
			int morningEnd = MorningEndHour * 10000 + MorningEndMinute * 100;
			int afternoonStart = AfternoonStartHour * 10000 + AfternoonStartMinute * 100;
			int end = TradeEndHour * 10000 + TradeEndMinute * 100;
			bool morning = t >= start && t <= morningEnd;
			bool afternoon = t >= afternoonStart && t <= end;
			if (!morning && !afternoon)
			{
				safetyStatus = "FUERA DE HORARIO";
				return false;
			}
			return true;
		}

		private bool IsMorning(DateTime ny)
		{
			int t = ny.Hour * 10000 + ny.Minute * 100;
			int morningEnd = MorningEndHour * 10000 + MorningEndMinute * 100;
			return t <= morningEnd;
		}

		private bool IsFlattenTime(DateTime ny)
		{
			int t = ny.Hour * 10000 + ny.Minute * 100;
			int flat = FlattenHour * 10000 + FlattenMinute * 100;
			return t >= flat;
		}

		private bool PassesInstrumentChecks()
		{
			if (ForceMNQ)
			{
				string name = Instrument != null && Instrument.MasterInstrument != null
					? Instrument.MasterInstrument.Name : string.Empty;
				if (name.IndexOf("MNQ", StringComparison.OrdinalIgnoreCase) < 0)
				{
					safetyStatus = "PON MNQ 5 MIN";
					return false;
				}
			}

			if (ForceFiveMinute)
			{
				if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 5)
				{
					safetyStatus = "PON GRAFICO 5 MIN";
					return false;
				}
			}
			return true;
		}

		private bool CanTradeAccount()
		{
			if (State != State.Realtime)
				return true;
			if (AllowLiveAccounts)
				return true;

			string acct = Account != null ? (Account.Name ?? string.Empty) : string.Empty;
			bool sim = acct.IndexOf("SIM", StringComparison.OrdinalIgnoreCase) >= 0
				|| acct.IndexOf("PLAYBACK", StringComparison.OrdinalIgnoreCase) >= 0
				|| acct.IndexOf("DEMO", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!sim)
			{
				safetyStatus = "SOLO SIM — activa Allow Live";
				return false;
			}
			return true;
		}

		private DateTime ToNy(DateTime t)
		{
			try { return TimeZoneInfo.ConvertTime(t, easternZone ?? TimeZoneInfo.Local); }
			catch { return t; }
		}

		private bool IsFriday(DateTime ny)
		{
			return ny.DayOfWeek == DayOfWeek.Friday;
		}

		private double GetTickValue()
		{
			if (Instrument == null || Instrument.MasterInstrument == null)
				return 0.50;
			return Instrument.MasterInstrument.PointValue * TickSize;
		}

		private string StateFilePath()
		{
			string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			string live = Path.Combine(dir, "NinjaTrader 8");
			if (!Directory.Exists(live))
				live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					"OneDrive", "Documentos", "NinjaTrader 8");
			return Path.Combine(live, "TradeifySelect25kBotV2_days.csv");
		}

		private void LoadPersistedDays()
		{
			if (stateLoaded || !PersistChallengeDays)
				return;
			if (State == State.Historical)
				return;

			stateLoaded = true;
			try
			{
				string path = StateFilePath();
				if (!File.Exists(path))
					return;
				string[] lines = File.ReadAllLines(path);
				for (int i = 0; i < lines.Length; i++)
				{
					string[] p = lines[i].Split(',');
					if (p.Length < 2)
						continue;
					double v;
					if (double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out v))
					{
						dayKeys.Add(p[0].Trim());
						dayPnls.Add(v);
					}
				}
			}
			catch { }
		}

		private void SavePersistedDays()
		{
			if (!PersistChallengeDays || State != State.Realtime)
				return;
			try
			{
				string today = ToNy(Time[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				int idx = dayKeys.IndexOf(today);
				if (idx >= 0)
					dayPnls[idx] = dailyPnL;
				else
				{
					dayKeys.Add(today);
					dayPnls.Add(dailyPnL);
				}

				using (StreamWriter w = new StreamWriter(StateFilePath(), false))
				{
					for (int i = 0; i < dayKeys.Count; i++)
						w.WriteLine(dayKeys[i] + "," + dayPnls[i].ToString("0.00", CultureInfo.InvariantCulture));
				}
			}
			catch { }
		}

		private void DrawStatusPanel(DateTime ny)
		{
			if (ChartControl == null)
				return;

			string dir = lastSignalDirection > 0 ? "LONG" : lastSignalDirection < 0 ? "SHORT" : "FLAT";
			string phase = Phase == TradeifySelectPhaseV2.Evaluation ? "EVAL SELECT 25K" : "FUNDED DAILY";
			string tradeLine = Position.MarketPosition == MarketPosition.Flat
				? (waitingForBreak ? "ARMADO — ESPERANDO ROMPER TRANCA" : "SIN POSICION")
				: "EN TRADE " + barsInTrade + "/" + MaxBarsInTrade + " velas"
					+ " | stop " + stopTicksPlanned + "t"
					+ (scalpFilled ? " | T1 COBRADO" : "")
					+ (breakEvenMoved ? " | BE" : "")
					+ (trailingActive ? " | TRAIL" : "");

			string text =
				"TRADEIFY SELECT 25K V2 | " + marketBias + " | " + dir + "\n"
				+ phase + " | " + ny.ToString("HH:mm", CultureInfo.InvariantCulture) + " ET\n"
				+ lastSignalReason + "\n"
				+ tradeLine + "\n"
				+ "Hoy: $" + dailyPnL.ToString("0.00", CultureInfo.InvariantCulture)
				+ " / " + DailyProfitTarget
				+ " | Challenge: $" + challengePnL.ToString("0.00", CultureInfo.InvariantCulture)
				+ " / " + ChallengeProfitTarget + "\n"
				+ "Dias verdes: " + greenDaysCount + "/" + MinTradingDays
				+ " | Trades: " + tradesToday + "/" + (IsFriday(ny) ? MaxTradesFriday : MaxTradesPerDay) + "\n"
				+ safetyStatus;

			Brush c = challengeLocked ? Brushes.Red
				: dailyLocked ? Brushes.Gold
				: lastSignalDirection > 0 ? Brushes.LimeGreen
				: lastSignalDirection < 0 ? Brushes.IndianRed
				: Brushes.Gainsboro;

			Draw.TextFixed(this, "TF25V2_Panel", text, TextPosition.BottomRight, c,
				new SimpleFont("Segoe UI", 11), Brushes.Black, Brushes.DimGray, 90);
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution.Order == null || execution.Order.OrderState != OrderState.Filled)
				return;

			string name = execution.Order.Name ?? string.Empty;

			bool isEntryFill = name == scalpName || name == runName;
			if (isEntryFill)
			{
				if (entryPrice <= 0)
					entryPrice = price > 0 ? price : Position.AveragePrice;
				return;
			}

			if (scalpFilled || string.IsNullOrEmpty(scalpName))
				return;

			string from = execution.Order.FromEntrySignal ?? string.Empty;
			if (from != scalpName)
				return;

			if (entryPrice <= 0)
				entryPrice = Position.AveragePrice;

			bool bankedGreen = tradeDirection > 0 ? price > entryPrice : price < entryPrice;
			if (!bankedGreen || entryPrice <= 0)
				return;

			scalpFilled = true;
			safetyStatus = "T1 COBRADO — RUNNER PROTEGIDO";

			if (runQty > 0 && !string.IsNullOrEmpty(runName))
			{
				double be = RoundTick(tradeDirection > 0
					? entryPrice + BreakEvenPlusTicks * TickSize
					: entryPrice - BreakEvenPlusTicks * TickSize);

				if (TightenRunnerStop(be))
					breakEvenMoved = true;
			}
		}

		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (marketPosition != MarketPosition.Flat)
				return;

			double tradePnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - tradeStartCumProfit;
			if (tradePnL < -0.01)
			{
				consecutiveLosses++;
				pauseUntilBar = Math.Max(pauseUntilBar, CurrentBar + 3);
			}
			else if (tradePnL > 0.01)
			{
				consecutiveLosses = 0;
			}

			ResetTradeTracking();
			SavePersistedDays();
		}

		private void ResetTradeTracking()
		{
			scalpName = string.Empty;
			runName = string.Empty;
			stopTicksPlanned = 0;
			scalpTicksPlanned = 0;
			runnerTicksPlanned = 0;
			scalpQty = 0;
			runQty = 0;
			tradeQtyPlanned = 0;
			tradeDirection = 0;
			entryPrice = 0;
			breakEvenMoved = false;
			trailingActive = false;
			scalpFilled = false;
			trailingStopPrice = 0;
			barsInTrade = 0;
			exitPending = false;
		}

		private int Clamp(int v, int min, int max)
		{
			if (v < min) return min;
			if (v > max) return max;
			return v;
		}

		private struct SignalSnapshot
		{
			public int Direction;
			public int Score;
			public string Reason;
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Phase", Order = 1, GroupName = "01 Tradeify")]
		public TradeifySelectPhaseV2 Phase { get; set; }

		[NinjaScriptProperty]
		[Range(1000, 50000)]
		[Display(Name = "Account Start Balance", Order = 2, GroupName = "01 Tradeify")]
		public double AccountStartBalance { get; set; }

		[NinjaScriptProperty]
		[Range(500, 9000)]
		[Display(Name = "Challenge Profit Target $", Order = 3, GroupName = "01 Tradeify")]
		public double ChallengeProfitTarget { get; set; }

		[NinjaScriptProperty]
		[Range(200, 5000)]
		[Display(Name = "Max EOD Drawdown $", Order = 4, GroupName = "01 Tradeify")]
		public double MaxEodDrawdown { get; set; }

		[NinjaScriptProperty]
		[Range(20, 50)]
		[Display(Name = "Consistency %", Order = 5, GroupName = "01 Tradeify")]
		public int ConsistencyPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Min Trading Days", Order = 6, GroupName = "01 Tradeify")]
		public int MinTradingDays { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Simulate Challenge In Backtest", Order = 7, GroupName = "01 Tradeify")]
		public bool SimulateChallengeInBacktest { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Contracts", Order = 1, GroupName = "02 Size")]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Max Contracts", Order = 2, GroupName = "02 Size")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Max Trades Per Day", Order = 3, GroupName = "02 Size")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Max Trades Friday", Order = 4, GroupName = "02 Size")]
		public int MaxTradesFriday { get; set; }

		[NinjaScriptProperty]
		[Range(20, 200)]
		[Display(Name = "Scalp Profit $ (techo de T1)", Order = 5, GroupName = "02 Size")]
		public double ScalpProfitDollars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Usar Runner (2do contrato)", Order = 6, GroupName = "02 Size")]
		public bool UseRunnerLeg { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enter On Stall Candle", Order = 7, GroupName = "02 Size")]
		public bool AllowEnterOnStallCandle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow Shorts", Order = 8, GroupName = "02 Size")]
		public bool AllowShorts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade With Trend (VWAP+EMA)", Order = 9, GroupName = "02 Size")]
		public bool TradeWithTrend { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VWAP Bounce", Order = 10, GroupName = "02 Size")]
		public bool UseVwapBounce { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use OR Retest", Order = 11, GroupName = "02 Size")]
		public bool UseOrRetest { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Stall Pattern", Order = 12, GroupName = "02 Size")]
		public bool UseStallPattern { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use OR Breakout (chase)", Order = 13, GroupName = "02 Size")]
		public bool UseOrBreakout { get; set; }

		[NinjaScriptProperty]
		[Range(0.15, 1.0)]
		[Display(Name = "Cuerpo Caida x ATR", Order = 14, GroupName = "02 Size")]
		public double ImpulseBodyAtr { get; set; }

		[NinjaScriptProperty]
		[Range(0.3, 0.95)]
		[Display(Name = "Stall Range Ratio", Order = 15, GroupName = "02 Size")]
		public double StallRangeRatio { get; set; }

		[NinjaScriptProperty]
		[Range(0.2, 0.8)]
		[Display(Name = "Stall Body Ratio", Order = 16, GroupName = "02 Size")]
		public double StallBodyRatio { get; set; }

		[NinjaScriptProperty]
		[Range(2, 12)]
		[Display(Name = "Stall Timeout Bars", Order = 17, GroupName = "02 Size")]
		public int StallTimeoutBars { get; set; }

		[NinjaScriptProperty]
		[Range(1.5, 6.0)]
		[Display(Name = "Runner Target (R)", Order = 18, GroupName = "02 Size")]
		public double RunnerTargetMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(50, 2000)]
		[Display(Name = "Daily Profit Target $", Order = 1, GroupName = "03 Risk")]
		public double DailyProfitTarget { get; set; }

		[NinjaScriptProperty]
		[Range(100, 2000)]
		[Display(Name = "Eval Daily Hard Cap $", Order = 2, GroupName = "03 Risk")]
		public double EvalDailyHardCap { get; set; }

		[NinjaScriptProperty]
		[Range(-2000, -50)]
		[Display(Name = "Eval Daily Loss $", Order = 3, GroupName = "03 Risk")]
		public double DailyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(-2000, -50)]
		[Display(Name = "Funded Daily Loss $", Order = 4, GroupName = "03 Risk")]
		public double FundedDailyLossLimit { get; set; }

		[NinjaScriptProperty]
		[Range(1, 6)]
		[Display(Name = "Stop After Consecutive Losses", Order = 5, GroupName = "03 Risk")]
		public int StopAfterConsecutiveLosses { get; set; }

		[NinjaScriptProperty]
		[Range(20, 400)]
		[Display(Name = "Risk Per Trade $", Order = 6, GroupName = "03 Risk")]
		public double RiskPerTradeDollars { get; set; }

		[NinjaScriptProperty]
		[Range(0.8, 3.5)]
		[Display(Name = "Scalp Target (R)", Order = 7, GroupName = "03 Risk")]
		public double ScalpTargetR { get; set; }

		[NinjaScriptProperty]
		[Range(10, 120)]
		[Display(Name = "Min Stop Ticks", Order = 8, GroupName = "03 Risk")]
		public int MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(20, 160)]
		[Display(Name = "Max Stop Ticks", Order = 9, GroupName = "03 Risk")]
		public int MaxStopTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.2, 1.5)]
		[Display(Name = "ATR Stop Multiplier", Order = 10, GroupName = "03 Risk")]
		public double AtrStopMultiplier { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Structure Stop", Order = 11, GroupName = "03 Risk")]
		public bool UseStructureStop { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10)]
		[Display(Name = "Structure Stop Buffer Ticks", Order = 12, GroupName = "03 Risk")]
		public int StructureStopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 2.0)]
		[Display(Name = "Move To BE At R", Order = 13, GroupName = "03 Risk")]
		public double MoveToBreakEvenAtR { get; set; }

		[NinjaScriptProperty]
		[Range(0, 20)]
		[Display(Name = "Break Even Plus Ticks", Order = 14, GroupName = "03 Risk")]
		public int BreakEvenPlusTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 3.0)]
		[Display(Name = "Start Trail At R", Order = 15, GroupName = "03 Risk")]
		public double StartTrailAtR { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 3.0)]
		[Display(Name = "Trail ATR Multiplier", Order = 16, GroupName = "03 Risk")]
		public double TrailAtrMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(4, 40)]
		[Display(Name = "Max Bars In Trade", Order = 17, GroupName = "03 Risk")]
		public int MaxBarsInTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Min Bars In Trade", Order = 18, GroupName = "03 Risk")]
		public int MinBarsInTrade { get; set; }

		[NinjaScriptProperty]
		[Range(50, 95)]
		[Display(Name = "Min Signal Score", Order = 1, GroupName = "04 Signal")]
		public int MinSignalScore { get; set; }

		[NinjaScriptProperty]
		[Range(1, 6)]
		[Display(Name = "Opening Range Bars", Order = 2, GroupName = "04 Signal")]
		public int OpeningRangeBars { get; set; }

		[NinjaScriptProperty]
		[Range(10, 50)]
		[Display(Name = "Mid EMA", Order = 4, GroupName = "04 Signal")]
		public int MidEmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(20, 100)]
		[Display(Name = "Slow EMA", Order = 5, GroupName = "04 Signal")]
		public int SlowEmaPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(8, 40)]
		[Display(Name = "ATR Period", Order = 6, GroupName = "04 Signal")]
		public int AtrPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(8, 30)]
		[Display(Name = "ADX Period", Order = 7, GroupName = "04 Signal")]
		public int AdxPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(10, 40)]
		[Display(Name = "Min ADX", Order = 8, GroupName = "04 Signal")]
		public int MinAdx { get; set; }

		[NinjaScriptProperty]
		[Range(8, 30)]
		[Display(Name = "RSI Period", Order = 9, GroupName = "04 Signal")]
		public int RsiPeriod { get; set; }

		[NinjaScriptProperty]
		[Range(50, 90)]
		[Display(Name = "RSI Overbought", Order = 10, GroupName = "04 Signal")]
		public double RsiOverbought { get; set; }

		[NinjaScriptProperty]
		[Range(10, 50)]
		[Display(Name = "RSI Oversold", Order = 11, GroupName = "04 Signal")]
		public double RsiOversold { get; set; }

		[NinjaScriptProperty]
		[Range(8, 40)]
		[Display(Name = "Volume Period", Order = 12, GroupName = "04 Signal")]
		public int VolumePeriod { get; set; }

		[NinjaScriptProperty]
		[Range(0.3, 2.0)]
		[Display(Name = "Volume Min Ratio (caida)", Order = 13, GroupName = "04 Signal")]
		public double VolumeMinRatio { get; set; }

		[NinjaScriptProperty]
		[Range(20, 250)]
		[Display(Name = "Max Extension From VWAP Ticks", Order = 14, GroupName = "04 Signal")]
		public int MaxExtensionFromVwapTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 40)]
		[Display(Name = "Min OR Range Points", Order = 15, GroupName = "04 Signal")]
		public double MinOrRangePoints { get; set; }

		[NinjaScriptProperty]
		[Range(20, 250)]
		[Display(Name = "Max OR Range Points", Order = 16, GroupName = "04 Signal")]
		public double MaxOrRangePoints { get; set; }

		[NinjaScriptProperty]
		[Range(2, 20)]
		[Display(Name = "VWAP Tag Ticks", Order = 17, GroupName = "04 Signal")]
		public int VwapTagTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Trade Start Hour", Order = 1, GroupName = "05 Session ET")]
		public int TradeStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Trade Start Minute", Order = 2, GroupName = "05 Session ET")]
		public int TradeStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Morning End Hour", Order = 3, GroupName = "05 Session ET")]
		public int MorningEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Morning End Minute", Order = 4, GroupName = "05 Session ET")]
		public int MorningEndMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Afternoon Start Hour", Order = 5, GroupName = "05 Session ET")]
		public int AfternoonStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Afternoon Start Minute", Order = 6, GroupName = "05 Session ET")]
		public int AfternoonStartMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Trade End Hour", Order = 7, GroupName = "05 Session ET")]
		public int TradeEndHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Trade End Minute", Order = 8, GroupName = "05 Session ET")]
		public int TradeEndMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Flatten Hour", Order = 9, GroupName = "05 Session ET")]
		public int FlattenHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Flatten Minute", Order = 10, GroupName = "05 Session ET")]
		public int FlattenMinute { get; set; }

		[NinjaScriptProperty]
		[Range(0, 23)]
		[Display(Name = "Friday Stop Hour", Order = 11, GroupName = "05 Session ET")]
		public int FridayStopHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Friday Stop Minute", Order = 12, GroupName = "05 Session ET")]
		public int FridayStopMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Force MNQ Only", Order = 1, GroupName = "06 Safety")]
		public bool ForceMNQ { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Force 5 Minute", Order = 2, GroupName = "06 Safety")]
		public bool ForceFiveMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow Live Accounts", Order = 3, GroupName = "06 Safety")]
		public bool AllowLiveAccounts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Persist Challenge Days", Order = 4, GroupName = "06 Safety")]
		public bool PersistChallengeDays { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw Panel", Order = 1, GroupName = "07 Visual")]
		public bool DrawPanel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw OR Lines", Order = 2, GroupName = "07 Visual")]
		public bool DrawOrLines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Imprimir Embudo (Output)", Order = 3, GroupName = "07 Visual")]
		public bool ShowFunnelDiagnostics { get; set; }

		#endregion
	}
}
