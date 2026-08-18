/*
 * Торговый робот для OsEngine: хеджированный робот на опционах Put и фьючерсах SOL (Bybit)
 *
 * Идея алгоритма:
 * 1) Покупаем опцион Put со страйком на один шаг ниже центрального страйка
 *    (например, центральный = 75 -> страйк опциона = 74) и одновременно 1 фьючерс SOL.
 * 2) Выставляем тейк-профит по фьючерсу: цена входа + цена опциона + комиссии входа/выхода.
 * 3) Если цена SOL падает на N USDT (параметр PriceDropStep) - докупаем ещё 1 фьючерс
 *    и новый опцион Put (всегда на один страйк ниже центрального, без дубликатов контрактов).
 * 4) После каждой доливки пересчитываем средний тейк по всему объёму фьючерсов:
 *    TP = AvgEntry + (SumCostOptions + SumCommissions) / FuturesVolume
 * 5) При достижении тейка продаём все опционы по принципу FIFO (от самого первого купленного)
 *    и закрываем весь объём фьючерсов рыночными ордерами.
 */

using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace OsEngine.Robots.SolanaOptions
{
    /// <summary>
    /// Робот: опционы Put SOL + хеджирование фьючерсом, доливка вниз, единый средний тейк
    /// </summary>
    [Bot("SolanaPutOptionsHedgedRobot")] // атрибут регистрирует робота в OsEngine (фабрика не нужна)
    public class SolanaPutOptionsHedgedRobot : BotPanel
    {
        // ===================== Параметры робота =====================

        /// <summary>Режим работы робота: Off / On</summary>
        private StrategyParameterString _regime;

        /// <summary>Название фьючерса SOL на Bybit (линейный перпетуал)</summary>
        private StrategyParameterString _futuresSecurityName;

        /// <summary>Базовый актив опционов (SOL)</summary>
        private StrategyParameterString _optionBaseAsset;

        /// <summary>Центральный страйк. 0 = вычислять автоматически (ближайший к текущей цене)</summary>
        private StrategyParameterDecimal _centralStrikeOverride;

        /// <summary>Шаг страйка: целевой страйк = центральный - этот шаг (обычно 1 USDT)</summary>
        private StrategyParameterDecimal _strikeStep;

        /// <summary>Минимальное число дней до экспирации опциона (2-дневные опционы => 1..3)</summary>
        private StrategyParameterInt _optionMinDaysToExpiry;

        /// <summary>Максимальное число дней до экспирации опциона</summary>
        private StrategyParameterInt _optionMaxDaysToExpiry;

        /// <summary>Объём фьючерса на один шаг (первый вход и доливка)</summary>
        private StrategyParameterDecimal _futuresVolumePerStep;

        /// <summary>Количество опционных контрактов на один шаг</summary>
        private StrategyParameterDecimal _optionLotsPerStep;

        /// <summary>Шаг доливки: при падении цены на это значение докупаем (1 USDT)</summary>
        private StrategyParameterDecimal _priceDropStep;

        /// <summary>Максимальное число шагов (первый вход + доливки)</summary>
        private StrategyParameterInt _maxSteps;

        /// <summary>Автоматически выбирать целевой опцион. false - использовать инструмент, заданный на вкладке опциона вручную (удобно для тестера)</summary>
        private StrategyParameterBool _useDynamicOptionSelection;

        // ===================== Вкладки =====================

        /// <summary>Вкладка фьючерса SOL (котировки, позиция, тейк-контроль)</summary>
        private BotTabSimple _tabFutures;

        /// <summary>Вкладка опциона (покупка/продажа опционов Put)</summary>
        private BotTabSimple _tabOptions;

        // ===================== Состояние робота =====================

        private readonly object _locker = new object();

        /// <summary>Запись о купленном опционе. Список ведётся в порядке покупки (FIFO для продажи)</summary>
        private class BoughtOption
        {
            public string SecurityName;  // имя контракта, например SOL-28MAR25-74-P
            public Security Security;    // сам инструмент (для переключения подписки при продаже)
            public Position Position;    // открытая позиция на вкладке опциона
            public decimal Cost;         // затраты на покупку: цена входа * объём
        }

        /// <summary>Список купленных опционов (порядок покупки = порядок продажи FIFO)</summary>
        private List<BoughtOption> _boughtOptions = new List<BoughtOption>();

        /// <summary>Суммарные затраты на все купленные опционы</summary>
        private decimal _totalOptionCost;

        /// <summary>Оплаченные комиссии (вход/выход по фьючерсу и опционам) на основе фактических заполнений</summary>
        private decimal _futuresBuyCommission;
        private decimal _futuresCloseCommission;
        private decimal _optionsBuyCommission;
        private decimal _optionsCloseCommission;

        /// <summary>Цена последнего заполнения покупки фьючерса (точка отсчёта для доливки)</summary>
        private decimal _lastFuturesFillPrice;

        /// <summary>Текущий тейк-профит по фьючерсу</summary>
        private decimal _takeProfit;

        /// <summary>Количество выполненных шагов (первый вход + доливки)</summary>
        private int _stepsDone;

        /// <summary>Количество ожидающих заполнения покупок (защита от повторных входов)</summary>
        private int _futuresBuysPending;
        private int _optionBuysPending;

        /// <summary>Идёт ли выход по тейку (блокирует новые входы и доливки)</summary>
        private bool _exitInProgress;

        /// <summary>Индекс следующего опциона для продажи (FIFO)</summary>
        private int _exitOptionIndex;

        /// <summary>Ждём ли закрытия текущего опциона при выходе</summary>
        private bool _optionSellPending;

        /// <summary>Время последнего сканирования списка опционов (троттлинг)</summary>
        private DateTime _lastOptionScanTime = DateTime.MinValue;

        /// <summary>Время последнего предупреждения об отсутствии опционов</summary>
        private DateTime _lastNoOptionLogTime = DateTime.MinValue;

        // ===================== Конструктор =====================

        public SolanaPutOptionsHedgedRobot(string name, StartProgram startProgram) : base(name, startProgram)
        {
            // ---- параметры робота ----
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On" });
            _futuresSecurityName = CreateParameter("FuturesSecurityName", "SOLUSDT.P");
            _optionBaseAsset = CreateParameter("OptionBaseAsset", "SOL");
            _centralStrikeOverride = CreateParameter("CentralStrikeOverride", 0m, 0m, 500m, 0.5m);
            _strikeStep = CreateParameter("StrikeStep", 1m, 0.5m, 10m, 0.5m);
            _optionMinDaysToExpiry = CreateParameter("OptionMinDaysToExpiry", 1, 0, 7, 1);
            _optionMaxDaysToExpiry = CreateParameter("OptionMaxDaysToExpiry", 3, 1, 30, 1);
            _futuresVolumePerStep = CreateParameter("FuturesVolumePerStep", 1m, 0.001m, 100m, 0.001m);
            _optionLotsPerStep = CreateParameter("OptionLotsPerStep", 1m, 0.01m, 100m, 0.01m);
            _priceDropStep = CreateParameter("PriceDropStep", 1m, 0.5m, 10m, 0.5m);
            _maxSteps = CreateParameter("MaxSteps", 5, 1, 20, 1);
            _useDynamicOptionSelection = CreateParameter("UseDynamicOptionSelection", true);

            // ---- создаём две вкладки: фьючерс и опцион ----
            TabCreate(BotTabType.Simple);
            TabCreate(BotTabType.Simple);

            _tabFutures = TabsSimple[0];
            _tabOptions = TabsSimple[1];

            // ---- события фьючерсной вкладки ----
            _tabFutures.NewTickEvent += Futures_NewTickEvent;                  // основной цикл логики
            _tabFutures.MyTradeEvent += Futures_MyTradeEvent;                  // фактические цены заполнений
            _tabFutures.PositionOpeningSuccesEvent += Futures_PositionOpeningSuccesEvent;
            _tabFutures.PositionOpeningFailEvent += (pos) => { if (_futuresBuysPending > 0) _futuresBuysPending--; };
            _tabFutures.PositionClosingSuccesEvent += Futures_PositionClosingSuccesEvent;
            _tabFutures.PositionClosingFailEvent += (pos) =>
            {
                SendNewLogMessage("Не удалось закрыть фьючерсную позицию", LogMessageType.Error);
            };

            // ---- события опционной вкладки ----
            _tabOptions.NewTickEvent += Options_NewTickEvent;                  // продвижение выхода по тейку
            _tabOptions.PositionOpeningSuccesEvent += Options_PositionOpeningSuccesEvent;
            _tabOptions.PositionOpeningFailEvent += (pos) => { if (_optionBuysPending > 0) _optionBuysPending--; };
            _tabOptions.PositionClosingSuccesEvent += Options_PositionClosingSuccesEvent;
            _tabOptions.PositionClosingFailEvent += (pos) =>
            {
                _optionSellPending = false; // разрешаем повторить попытку продажи на следующем тике
                SendNewLogMessage("Не удалось закрыть опцион " + pos.SecurityName, LogMessageType.Error);
            };

            // ---- автоматически привязываем вкладки к серверу Bybit, если пользователь ещё не настроил их ----
            BindTabsToBybitServerIfNeeded();

            Description = "Робот покупает опционы Put SOL и фьючерс, докупает при падении цены и закрывает всё по среднему тейк-профиту.";
        }

        // ===================== Инициализация подключения =====================

        /// <summary>
        /// Если пользователь не выбрал сервер для вкладок - привязываем их к серверу Bybit.
        /// Фьючерс выбирается в настройках вкладки (стандартный UI OsEngine), либо используется параметр по умолчанию.
        /// </summary>
        private void BindTabsToBybitServerIfNeeded()
        {
            try
            {
                if (StartProgram != StartProgram.IsOsTrader)
                {
                    return; // в тестере/оптимизаторе вкладки настраивает сам OsTester
                }

                IServer server = ServerMaster.GetServers()
                    .FirstOrDefault(s => s.ServerType == ServerType.Bybit);

                if (server == null)
                {
                    SendNewLogMessage("Сервер Bybit не найден. Создайте сервер Bybit в OsTrader и включите у него опцию 'Use Options'.", LogMessageType.System);
                    return;
                }

                string serverFullName = server.ServerType.ToString();
                if (server is AServer serverA)
                {
                    serverFullName = serverA.ServerNameUnique;
                }

                foreach (BotTabSimple tab in TabsSimple)
                {
                    if (tab == null || tab.Connector.ServerType != ServerType.None)
                    {
                        continue; // вкладка уже настроена пользователем - не трогаем
                    }

                    tab.Connector.ServerType = ServerType.Bybit;
                    tab.Connector.ServerFullName = serverFullName;
                    tab.Connector.PortfolioName = "BybitUNIFIED";
                }

                if (string.IsNullOrEmpty(_tabFutures.Connector.SecurityName))
                {
                    _tabFutures.Connector.SecurityName = _futuresSecurityName.ValueString;
                    _tabFutures.Connector.SecurityClass = "LINEAR_PER_PERP";
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        // ===================== Поиск целевого опциона =====================

        /// <summary>
        /// Находит опцион Put для покупки:
        /// - страйк: на StrikeStep ниже центрального страйка (центральный - вручную или ближайший к цене SOL);
        /// - экспирация: ближайшая к "2-дневной" в окне [MinDays..MaxDays];
        /// - исключает уже купленные контракты (защита от дубликатов).
        /// </summary>
        private Security GetTargetOption()
        {
            try
            {
                IServer server = _tabFutures.Connector.MyServer;
                if (server == null || server.Securities == null)
                {
                    return null;
                }

                string basePrefix = _optionBaseAsset.ValueString + "-";
                DateTime now = DateTime.UtcNow;
                DateTime minExp = now.AddDays(_optionMinDaysToExpiry.ValueInt);
                DateTime maxExp = now.AddDays(_optionMaxDaysToExpiry.ValueInt);

                // все Put-опционы по базовому активу в нужном окне экспирации
                List<Security> puts = server.Securities
                    .Where(s => s != null
                        && s.SecurityType == SecurityType.Option
                        && s.OptionType == OptionType.Put
                        && !string.IsNullOrEmpty(s.Name)
                        && s.Name.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(s.NameClass)
                        && s.NameClass.EndsWith("_Options")
                        && s.Strike > 0
                        && s.Expiration >= minExp
                        && s.Expiration <= maxExp)
                    .ToList();

                if (puts.Count == 0)
                {
                    return null;
                }

                // --- центральный страйк ---
                decimal price = _tabFutures.PriceBestAsk > 0 ? _tabFutures.PriceBestAsk : _lastFuturesFillPrice;
                decimal centralStrike;

                if (_centralStrikeOverride.ValueDecimal > 0)
                {
                    // ручная фиксация центрального страйка
                    centralStrike = _centralStrikeOverride.ValueDecimal;
                }
                else if (price > 0)
                {
                    // автоматически: ближайший доступный страйк к текущей цене SOL
                    centralStrike = puts.MinBy(s => Math.Abs(s.Strike - price)).Strike;
                }
                else
                {
                    return null;
                }

                // --- целевой страйк: на один шаг ниже центрального ---
                decimal targetStrike = puts.MinBy(s => Math.Abs(s.Strike - (centralStrike - _strikeStep.ValueDecimal))).Strike;

                // --- экспирация: ближайшая к now + 2 дня (2-дневные опционы) ---
                List<Security> candidates = puts.Where(s => s.Strike == targetStrike).ToList();
                DateTime idealExpiry = now.AddDays(2);
                candidates.Sort((a, b) =>
                    (a.Expiration - idealExpiry).Duration().CompareTo((b.Expiration - idealExpiry).Duration()));

                // --- не покупаем контракт, который уже куплен ---
                Security target = candidates.FirstOrDefault(s => !_boughtOptions.Any(o => o.SecurityName == s.Name));

                return target;
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return null;
            }
        }

        /// <summary>
        /// Поддерживает подписку вкладки опциона на текущий целевой контракт (с троттлингом).
        /// Без подписки вкладка не получает котировку, а рыночные ордера требуют BestAsk/BestBid.
        /// </summary>
        private void UpdateTargetOptionSubscription()
        {
            if (_exitInProgress)
            {
                return; // при выходе вкладкой управляет ExitNextOption
            }

            bool needScan = _lastOptionScanTime == DateTime.MinValue
                || DateTime.UtcNow - _lastOptionScanTime > TimeSpan.FromSeconds(5)
                || string.IsNullOrEmpty(_tabOptions.Connector.SecurityName);

            if (!needScan)
            {
                return;
            }

            _lastOptionScanTime = DateTime.UtcNow;

            Security target = GetTargetOption();
            if (target == null)
            {
                LogNoOptionWarning();
                return;
            }

            BindOptionTabToSecurity(target);
        }

        /// <summary>
        /// Переключает вкладку опциона на заданный контракт (подписка на котировки).
        /// </summary>
        private void BindOptionTabToSecurity(Security security)
        {
            if (security == null)
            {
                return;
            }

            if (_tabOptions.Connector.SecurityClass != security.NameClass)
            {
                _tabOptions.Connector.SecurityClass = security.NameClass;
            }

            if (_tabOptions.Connector.SecurityName != security.Name)
            {
                _tabOptions.Connector.SecurityName = security.Name;
                SendNewLogMessage("Вкладка опциона переключена на " + security.Name, LogMessageType.System);
            }
        }

        /// <summary>
        /// Ограниченное по времени предупреждение, если опционы не найдены.
        /// </summary>
        private void LogNoOptionWarning()
        {
            if (DateTime.UtcNow - _lastNoOptionLogTime > TimeSpan.FromSeconds(60))
            {
                _lastNoOptionLogTime = DateTime.UtcNow;
                SendNewLogMessage("Подходящие опционы Put не найдены. Проверьте: сервер Bybit, параметр 'Use Options' у сервера, окно экспирации и базовый актив.", LogMessageType.System);
            }
        }

        // ===================== Комиссии =====================

        /// <summary>
        /// Расчёт комиссии по операции: price * volume * rate% или фикс за лот.
        /// Ставка берётся из настроек вкладки (Connector.CommissionType / CommissionValue).
        /// </summary>
        private decimal CalcCommission(BotTabSimple tab, decimal price, decimal volume)
        {
            if (tab == null || tab.Connector == null || price <= 0 || volume <= 0)
            {
                return 0;
            }

            switch (tab.Connector.CommissionType)
            {
                case CommissionType.Percent:
                    return price * volume * tab.Connector.CommissionValue / 100m;
                case CommissionType.OneLotFix:
                    return volume * tab.Connector.CommissionValue;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Комиссия по ноционалу (для оценок выхода опционов).
        /// </summary>
        private decimal CalcCommissionOnNotional(BotTabSimple tab, decimal notional)
        {
            if (tab == null || tab.Connector == null || notional <= 0)
            {
                return 0;
            }

            if (tab.Connector.CommissionType == CommissionType.Percent)
            {
                return notional * tab.Connector.CommissionValue / 100m;
            }

            return 0;
        }

        /// <summary>
        /// Сумма комиссий по закрывающим сделкам позиции (использует фактические цены ордеров).
        /// </summary>
        private decimal CalcCommissionOnCloseOrders(BotTabSimple tab, Position position)
        {
            decimal sum = 0;

            for (int i = 0; position != null && position.CloseOrders != null && i < position.CloseOrders.Count; i++)
            {
                Order order = position.CloseOrders[i];
                if (order != null && order.VolumeExecute > 0)
                {
                    sum += CalcCommission(tab, order.Price, order.VolumeExecute);
                }
            }

            return sum;
        }

        // ===================== Расчёт тейк-профита =====================

        /// <summary>
        /// Средняя цена входа по всем открытым фьючерсным позициям (взвешенная по объёму).
        /// </summary>
        private decimal GetFuturesAverageEntry()
        {
            List<Position> longs = _tabFutures.PositionOpenLong;
            if (longs == null || longs.Count == 0)
            {
                return 0;
            }

            decimal notional = 0;
            decimal volume = 0;

            for (int i = 0; i < longs.Count; i++)
            {
                if (longs[i] == null || longs[i].OpenVolume <= 0)
                {
                    continue;
                }

                notional += longs[i].EntryPrice * longs[i].OpenVolume;
                volume += longs[i].OpenVolume;
            }

            return volume > 0 ? notional / volume : 0;
        }

        /// <summary>
        /// Пересчёт единого тейк-профита по всему объёму фьючерсов:
        /// TP = AvgEntry + (SumCostOptions + SumCommissions) / FuturesVolume
        /// Вызывается после каждой доливки и каждого входа.
        /// </summary>
        private void RecalcTakeProfit()
        {
            if (_exitInProgress)
            {
                return;
            }

            decimal futuresVolume = _tabFutures.VolumeNet;
            if (futuresVolume <= 0)
            {
                _takeProfit = 0;
                return;
            }

            decimal avgEntry = GetFuturesAverageEntry();
            if (avgEntry <= 0)
            {
                return;
            }

            // цена, по которой ориентировочно закроем фьючерс (текущая или средняя цена входа)
            decimal exitPrice = _tabFutures.PriceBestAsk > 0 ? _tabFutures.PriceBestAsk : avgEntry;

            // уже оплаченные комиссии (вход и выполненные закрытия) - по фактическим сделкам
            decimal paidCommissions = _futuresBuyCommission + _futuresCloseCommission
                                    + _optionsBuyCommission + _optionsCloseCommission;

            // расчётные комиссии на оставшийся выход: фьючерс по текущему объёму + опционы по их стоимости
            decimal exitCommissions = CalcCommission(_tabFutures, exitPrice, futuresVolume);
            exitCommissions += CalcCommissionOnNotional(_tabOptions, _totalOptionCost);

            decimal totalCommissions = paidCommissions + exitCommissions;

            // формула из ТЗ: средняя цена входа + (затраты на опционы + комиссии) / объём фьючерсов
            _takeProfit = avgEntry + (_totalOptionCost + totalCommissions) / futuresVolume;

            SendNewLogMessage(
                "Тейк пересчитан: AvgEntry=" + avgEntry.ToString("F2") +
                ", опционы=" + _totalOptionCost.ToString("F2") +
                ", комиссии=" + totalCommissions.ToString("F2") +
                ", объём=" + futuresVolume +
                " => TP=" + _takeProfit.ToString("F2"),
                LogMessageType.System);
        }

        // ===================== Вход в позицию =====================

        /// <summary>
        /// Общая процедура шага входа: покупка фьючерса + покупка опциона рыночными ордерами.
        /// Вызывается и для первого входа, и для доливок.
        /// </summary>
        private void TryEnterStep(string signalType)
        {
            // --- выбор целевого опциона ---
            if (_useDynamicOptionSelection.ValueBool)
            {
                Security target = GetTargetOption();
                if (target == null)
                {
                    LogNoOptionWarning();
                    return;
                }

                BindOptionTabToSecurity(target);
            }

            // --- проверки готовности вкладок ---
            if (_tabFutures.Security == null)
            {
                SendNewLogMessage("Фьючерс не загружен (" + _futuresSecurityName.ValueString + ")", LogMessageType.Error);
                return;
            }

            if (_tabOptions.Security == null)
            {
                SendNewLogMessage("Опцион ещё не загружен на вкладку. Ждём инструменты Bybit.", LogMessageType.System);
                return;
            }

            if (_tabOptions.PriceBestAsk <= 0)
            {
                SendNewLogMessage("Нет котировки по опциону " + _tabOptions.Security.Name + ", ждём тики.", LogMessageType.System);
                return;
            }

            // --- объёмы с округлением под шаг инструмента ---
            decimal futuresVol = RoundVolume(_futuresVolumePerStep.ValueDecimal, _tabFutures.Security);
            decimal optionVol = RoundVolume(_optionLotsPerStep.ValueDecimal, _tabOptions.Security);

            if (futuresVol <= 0 || optionVol <= 0)
            {
                SendNewLogMessage("Объём меньше минимального торгового объёма инструмента. Шаг пропущен.", LogMessageType.Error);
                return;
            }

            // --- 1) покупаем фьючерс рыночным ордером ---
            _futuresBuysPending++;
            Position futuresPos = _tabFutures.BuyAtMarket(futuresVol, signalType);

            if (futuresPos == null)
            {
                _futuresBuysPending--;
                SendNewLogMessage("Не удалось выставить покупку фьючерса. Шаг пропущен.", LogMessageType.Error);
                return;
            }

            // --- 2) покупаем опцион рыночным ордером ---
            _optionBuysPending++;
            Position optionPos = _tabOptions.BuyAtMarket(optionVol, signalType);

            if (optionPos == null)
            {
                _optionBuysPending--;
                SendNewLogMessage("Не удалось выставить покупку опциона. Закрываем фьючерс, чтобы не остаться без страховки.", LogMessageType.Error);
                _tabFutures.CloseAllAtMarket("EntryRollback");
                return;
            }

            SendNewLogMessage(
                signalType + ": куплены фьючерс " + futuresVol + " " + _tabFutures.Security.Name +
                " и опцион " + optionVol + " " + _tabOptions.Security.Name,
                LogMessageType.System);
        }

        /// <summary>
        /// Округление объёма до шага инструмента и проверка минимального объёма.
        /// </summary>
        private decimal RoundVolume(decimal volume, Security security)
        {
            if (volume <= 0 || security == null)
            {
                return 0;
            }

            int decimals = Math.Max(0, security.DecimalsVolume);
            decimal rounded = Math.Round(volume, decimals);

            if (security.MinTradeAmount > 0 && rounded < security.MinTradeAmount)
            {
                return 0;
            }

            return rounded;
        }

        // ===================== Выход по тейку (FIFO по опционам) =====================

        /// <summary>
        /// Старт выхода: продажа всех опционов от самого первого купленного (FIFO),
        /// затем закрытие всего объёма фьючерсов по рынку.
        /// </summary>
        private void TryExitByTakeProfit()
        {
            if (_exitInProgress)
            {
                return;
            }

            SendNewLogMessage(
                "Достигнут тейк-профит: цена=" + _tabFutures.PriceBestAsk.ToString("F2") +
                " >= TP=" + _takeProfit.ToString("F2") + ". Начинаем закрытие позиции.",
                LogMessageType.System);

            if (_boughtOptions.Count == 0)
            {
                // опционов нет (например, после отката) - просто закрываем фьючерсы
                _tabFutures.CloseAllAtMarket("TakeProfit");
                return;
            }

            _exitInProgress = true;
            _exitOptionIndex = 0;
            _optionSellPending = false;

            ExitNextOption();
        }

        /// <summary>
        /// Продаёт следующий опцион из списка FIFO.
        /// Перед продажей переключает вкладку опциона на продаваемый контракт,
        /// чтобы получить свежую котировку (рыночный ордер требует BestBid).
        /// </summary>
        private void ExitNextOption()
        {
            if (_optionSellPending)
            {
                return; // ждём закрытия текущего опциона
            }

            if (_exitOptionIndex >= _boughtOptions.Count)
            {
                // все опционы проданы - закрываем весь объём фьючерсов по рынку
                SendNewLogMessage("Все опционы проданы. Закрываем фьючерсную позицию по рынку.", LogMessageType.System);
                _tabFutures.CloseAllAtMarket("TakeProfit");
                return;
            }

            BoughtOption option = _boughtOptions[_exitOptionIndex];

            // подписываем вкладку на продаваемый контракт
            BindOptionTabToSecurity(option.Security);

            if (_tabOptions.PriceBestBid <= 0)
            {
                SendNewLogMessage("Ждём котировку по опциону " + option.SecurityName + " для закрытия.", LogMessageType.System);
                return; // тик придёт после переподписки - выход продолжит Options_NewTickEvent
            }

            _optionSellPending = true;
            _tabOptions.CloseAtMarket(option.Position, option.Position.OpenVolume, "TakeProfit");

            SendNewLogMessage("Продажа опциона " + option.SecurityName + " (FIFO, #" + (_exitOptionIndex + 1) + ")", LogMessageType.System);
        }

        // ===================== Завершение цикла =====================

        /// <summary>
        /// Если фьючерсов и опционов больше нет - цикл завершён, сбрасываем состояние.
        /// </summary>
        private void MaybeFinalizeCycle()
        {
            if (_tabFutures.VolumeNet <= 0 && _boughtOptions.Count == 0)
            {
                ResetCycle();
            }
        }

        /// <summary>
        /// Сброс состояния робота после полного закрытия цикла.
        /// </summary>
        private void ResetCycle()
        {
            _boughtOptions.Clear();
            _totalOptionCost = 0;
            _futuresBuyCommission = 0;
            _futuresCloseCommission = 0;
            _optionsBuyCommission = 0;
            _optionsCloseCommission = 0;
            _lastFuturesFillPrice = 0;
            _takeProfit = 0;
            _stepsDone = 0;
            _futuresBuysPending = 0;
            _optionBuysPending = 0;
            _exitInProgress = false;
            _optionSellPending = false;
            _exitOptionIndex = 0;

            SendNewLogMessage("Цикл завершён. Состояние робота сброшено, готов к новому циклу.", LogMessageType.System);
        }

        // ===================== Обработчики событий =====================

        /// <summary>
        /// Основной цикл робота (по каждому тику фьючерса):
        /// обновление целевого опциона -> первый вход -> проверка тейка -> доливка.
        /// </summary>
        private void Futures_NewTickEvent(Trade trade)
        {
            lock (_locker)
            {
                try
                {
                    if (_regime.ValueString != "On")
                    {
                        return;
                    }

                    if (_tabFutures.Security == null)
                    {
                        return;
                    }

                    decimal price = _tabFutures.PriceBestAsk;
                    if (price <= 0)
                    {
                        price = _tabFutures.PriceBestBid;
                    }
                    if (price <= 0)
                    {
                        return;
                    }

                    if (_exitInProgress)
                    {
                        return; // выход уже выполняется - новых входов не делаем
                    }

                    // 1) держим подписку на актуальный целевой опцион
                    if (_useDynamicOptionSelection.ValueBool)
                    {
                        UpdateTargetOptionSubscription();
                    }

                    bool noFuturesPosition = _tabFutures.VolumeNet <= 0;

                    // 2) первый вход: позиции нет и нет ожидающих покупок
                    if (noFuturesPosition && _futuresBuysPending <= 0)
                    {
                        TryEnterStep("FirstEntry");
                        return;
                    }

                    // 3) проверка тейк-профита: цена >= TP => выход
                    if (_takeProfit > 0 && price >= _takeProfit && !noFuturesPosition && _futuresBuysPending <= 0)
                    {
                        TryExitByTakeProfit();
                        return;
                    }

                    // 4) доливка: цена упала на PriceDropStep от последнего входа
                    if (_lastFuturesFillPrice > 0
                        && price <= _lastFuturesFillPrice - _priceDropStep.ValueDecimal
                        && !noFuturesPosition
                        && _futuresBuysPending <= 0
                        && _stepsDone < _maxSteps.ValueInt)
                    {
                        SendNewLogMessage(
                            "Цена " + price.ToString("F2") + " упала на " + _priceDropStep.ValueDecimal +
                            " от входа " + _lastFuturesFillPrice.ToString("F2") + ". Доливка №" + (_stepsDone + 1),
                            LogMessageType.System);

                        TryEnterStep("AddStep");
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Фактические заполнения фьючерса: фиксируем цену последней покупки и комиссию входа.
        /// </summary>
        private void Futures_MyTradeEvent(MyTrade trade)
        {
            lock (_locker)
            {
                try
                {
                    if (trade == null || trade.Side != Side.Buy)
                    {
                        return;
                    }

                    _lastFuturesFillPrice = trade.Price;
                    _futuresBuyCommission += CalcCommission(_tabFutures, trade.Price, trade.Volume);
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Фьючерсная позиция открыта (первый вход или доливка): пересчитываем тейк.
        /// </summary>
        private void Futures_PositionOpeningSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    if (_futuresBuysPending > 0)
                    {
                        _futuresBuysPending--;
                    }

                    _stepsDone++;
                    RecalcTakeProfit();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Фьючерсная позиция закрыта: фиксируем комиссию выхода; если всё закрыто - сброс цикла.
        /// </summary>
        private void Futures_PositionClosingSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    _futuresCloseCommission += CalcCommissionOnCloseOrders(_tabFutures, position);

                    if (_tabFutures.VolumeNet <= 0)
                    {
                        MaybeFinalizeCycle();
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Опцион куплен: заносим в список FIFO, учитываем затраты и комиссию, пересчитываем тейк.
        /// </summary>
        private void Options_PositionOpeningSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    if (_optionBuysPending > 0)
                    {
                        _optionBuysPending--;
                    }

                    decimal cost = position.EntryPrice * position.OpenVolume;
                    _totalOptionCost += cost;
                    _optionsBuyCommission += CalcCommission(_tabOptions, position.EntryPrice, position.OpenVolume);

                    _boughtOptions.Add(new BoughtOption
                    {
                        SecurityName = position.SecurityName,
                        Security = _tabOptions.Security,
                        Position = position,
                        Cost = cost
                    });

                    SendNewLogMessage(
                        "Куплен опцион " + position.SecurityName +
                        ", цена=" + position.EntryPrice.ToString("F4") +
                        ", объём=" + position.OpenVolume,
                        LogMessageType.System);

                    RecalcTakeProfit();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Опцион закрыт: убираем из списка; при выходе по тейку двигаемся к следующему (FIFO).
        /// </summary>
        private void Options_PositionClosingSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    _optionsCloseCommission += CalcCommissionOnCloseOrders(_tabOptions, position);

                    BoughtOption bought = _boughtOptions.FirstOrDefault(o => o.Position == position);
                    if (bought == null && position != null)
                    {
                        bought = _boughtOptions.FirstOrDefault(o => o.Position != null && o.Position.Number == position.Number);
                    }

                    if (bought != null)
                    {
                        _boughtOptions.Remove(bought);
                        SendNewLogMessage("Опцион закрыт: " + position.SecurityName, LogMessageType.System);
                    }

                    if (_exitInProgress)
                    {
                        // продали очередной опцион - переходим к следующему по FIFO
                        _optionSellPending = false;
                        _exitOptionIndex++;
                        ExitNextOption();
                    }
                    else
                    {
                        MaybeFinalizeCycle();
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Тики опционной вкладки: продвигают выход по тейку (продажа опционов FIFO по очереди).
        /// </summary>
        private void Options_NewTickEvent(Trade trade)
        {
            lock (_locker)
            {
                try
                {
                    if (_exitInProgress && !_optionSellPending && _boughtOptions.Count > 0)
                    {
                        ExitNextOption();
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        // ===================== Служебные методы =====================

        /// <summary>
        /// Имя типа робота (используется в настройках/журнале).
        /// </summary>
        public override string GetNameStrategyType()
        {
            return "SolanaPutOptionsHedgedRobot";
        }

        /// <summary>
        /// Окно с краткими инструкциями по настройке.
        /// </summary>
        public override void ShowIndividualSettingsDialog()
        {
            MessageBox.Show(
                "SolanaPutOptionsHedgedRobot\n\n" +
                "1. Создайте сервер Bybit и включите у него параметр 'Use Options'.\n" +
                "2. На вкладке фьючерса выберите инструмент SOLUSDT.P (Linear).\n" +
                "3. В настройках обеих вкладок задайте комиссии (CommissionType/CommissionValue) - они участвуют в расчёте тейка.\n" +
                "4. Установите Regime = On. Робот сам выбирает 2-дневный опцион Put на страйк ниже центрального.\n" +
                "5. При падении цены на PriceDropStep докупаются фьючерс и новый опцион (без дубликатов).\n" +
                "6. При достижении среднего тейка опционы продаются FIFO, фьючерс закрывается по рынку.\n\n" +
                "Для теста в OsTester: UseDynamicOptionSelection = false и задайте опцион на вкладке вручную.");
        }
    }
}
