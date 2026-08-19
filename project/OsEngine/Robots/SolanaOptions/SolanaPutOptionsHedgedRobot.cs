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
using System.Threading;
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

        /// <summary>Режим роста объёма последующих добавок: Fixed - фиксированный объём, Multiplier - кратно текущему</summary>
        private StrategyParameterString _volumeGrowthMode;

        /// <summary>Множитель объёма для режима Multiplier</summary>
        private StrategyParameterDecimal _volumeMultiplier;

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

        /// <summary>Нужно ли перевыставить лимитный тейк-ордер (старый снят/пропал/цена изменилась)</summary>
        private bool _tpDirty;

        /// <summary>Время последнего предупреждения о шортовой позиции</summary>
        private DateTime _lastShortLogTime = DateTime.MinValue;

        /// <summary>Время последнего предупреждения о дублях позиций в журнале</summary>
        private DateTime _lastDupLogTime = DateTime.MinValue;

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

        /// <summary>Время последнего предупреждения об отсутствии сервера Bybit</summary>
        private DateTime _lastNoServerLogTime = DateTime.MinValue;

        /// <summary>Время последней неудачной попытки входа (не было котировки опциона) - троттлинг попыток</summary>
        private DateTime _lastEntryGiveUpTime = DateTime.MinValue;

        /// <summary>Троттлинг сообщений о нехватке котировки при выходе</summary>
        private DateTime _lastExitQuoteLogTime = DateTime.MinValue;

        /// <summary>Вкладки уже привязаны к серверу Bybit (привязка выполняется, когда сервер появится)</summary>
        private bool _tabsBoundToServer;

        /// <summary>Состояние восстановлено после перезапуска (позиции из журнала подхвачены)</summary>
        private bool _stateRestored;

        /// <summary>Таймер повторной попытки привязки к серверу (пока серверы не загрузятся)</summary>
        private System.Threading.Timer _bindRetryTimer;

        // ===================== Конструктор =====================

        public SolanaPutOptionsHedgedRobot(string name, StartProgram startProgram) : base(name, startProgram)
        {
            // ---- параметры робота ----
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On" });
            _futuresSecurityName = CreateParameter("FuturesSecurityName", "SOLUSDT.P",
                new[] { "SOLUSDT.P", "BTCUSDT.P", "ETHUSDT.P" });
            _optionBaseAsset = CreateParameter("OptionBaseAsset", "SOL");
            _centralStrikeOverride = CreateParameter("CentralStrikeOverride", 0m, 0m, 500m, 0.5m);
            _strikeStep = CreateParameter("StrikeStep", 1m, 0.5m, 10m, 0.5m);
            _optionMinDaysToExpiry = CreateParameter("OptionMinDaysToExpiry", 1, 0, 7, 1);
            _optionMaxDaysToExpiry = CreateParameter("OptionMaxDaysToExpiry", 3, 1, 30, 1);
            _futuresVolumePerStep = CreateParameter("FuturesVolumePerStep", 1m, 0.001m, 100m, 0.001m);
            _optionLotsPerStep = CreateParameter("OptionLotsPerStep", 1m, 0.01m, 100m, 0.01m);
            _priceDropStep = CreateParameter("PriceDropStep", 1m, 0.5m, 10m, 0.5m);
            _maxSteps = CreateParameter("MaxSteps", 5, 1, 20, 1);
            _volumeGrowthMode = CreateParameter("VolumeGrowthMode", "Fixed", new[] { "Fixed", "Multiplier" });
            _volumeMultiplier = CreateParameter("VolumeMultiplier", 2m, 1m, 10m, 0.5m);
            _useDynamicOptionSelection = CreateParameter("UseDynamicOptionSelection", true);

            // ---- создаём две вкладки: фьючерс и опцион ----
            TabCreate(BotTabType.Simple);
            TabCreate(BotTabType.Simple);

            _tabFutures = TabsSimple[0];
            _tabOptions = TabsSimple[1];

            // ВАЖНО: тейк-лимит должен висеть на бирже бессрочно.
            // По умолчанию OsEngine (BotManualControl) принудительно закрывает позицию по рынку,
            // если ордер на закрытие не исполнился за SecondToClose (по умолчанию 50 секунд).
            // Отключаем это на вкладке фьючерса полностью: GTC для новых ордеров
            // и выключенный таймаут (иначе "просроченные" ордера из журнала после рестарта
            // всё равно будут принудительно закрыты).
            _tabFutures.ManualPositionSupport.OrderTypeTime = OrderTypeTime.GTC;
            _tabFutures.ManualPositionSupport.SecondToCloseIsOn = false;

            // ---- события фьючерсной вкладки ----
            _tabFutures.NewTickEvent += Futures_NewTickEvent;                  // основной цикл логики
            _tabFutures.MyTradeEvent += Futures_MyTradeEvent;                  // фактические цены заполнений
            _tabFutures.PositionOpeningSuccesEvent += Futures_PositionOpeningSuccesEvent;
            _tabFutures.PositionOpeningFailEvent += Futures_PositionOpeningFailEvent;
            _tabFutures.PositionClosingSuccesEvent += Futures_PositionClosingSuccesEvent;
            _tabFutures.PositionClosingFailEvent += Futures_PositionClosingFailEvent;

            // ---- события опционной вкладки ----
            _tabOptions.NewTickEvent += Options_NewTickEvent;                  // продвижение выхода по тейку
            _tabOptions.PositionOpeningSuccesEvent += Options_PositionOpeningSuccesEvent;
            _tabOptions.PositionOpeningFailEvent += Options_PositionOpeningFailEvent;
            _tabOptions.PositionClosingSuccesEvent += Options_PositionClosingSuccesEvent;
            _tabOptions.PositionClosingFailEvent += (pos) =>
            {
                _optionSellPending = false; // разрешаем повторить попытку продажи на следующем тике
                SendNewLogMessage("Не удалось закрыть опцион " + pos.SecurityName, LogMessageType.Error);
            };

            // ---- автоматически привязываем вкладки к серверу Bybit, если пользователь ещё не настроил их ----
            BindTabsToBybitServerIfNeeded();

            // серверы OsEngine загружаются асинхронно - повторяем привязку по таймеру, пока она не выполнится
            if (StartProgram == StartProgram.IsOsTrader)
            {
                _bindRetryTimer = new System.Threading.Timer(
                    _ => BindTabsToBybitServerRetry(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            }

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

                if (_tabsBoundToServer)
                {
                    return; // уже привязаны
                }

                // серверы могут быть ещё не загружены в момент создания робота - список может быть null
                List<IServer> servers = ServerMaster.GetServers();

                if (servers == null || servers.Count == 0)
                {
                    // привязка повторится автоматически по первому тику фьючерса
                    return;
                }

                IServer server = servers.FirstOrDefault(s => s.ServerType == ServerType.Bybit);

                if (server == null)
                {
                    LogBybitServerNotFound();
                    return;
                }

                string serverFullName = server.ServerType.ToString();
                if (server is AServer serverA)
                {
                    serverFullName = serverA.ServerNameUnique;
                }

                bool needReconnect = false;

                foreach (BotTabSimple tab in TabsSimple)
                {
                    if (tab == null || tab.Connector.ServerType != ServerType.None)
                    {
                        continue; // вкладка уже настроена пользователем - не трогаем
                    }

                    tab.Connector.ServerType = ServerType.Bybit;
                    tab.Connector.ServerFullName = serverFullName;
                    tab.Connector.PortfolioName = "BybitUNIFIED";
                    needReconnect = true;
                }

                if (string.IsNullOrEmpty(_tabFutures.Connector.SecurityName))
                {
                    _tabFutures.Connector.SecurityName = _futuresSecurityName.ValueString;
                    needReconnect = true;
                }

                if (needReconnect)
                {
                    _tabsBoundToServer = true;
                    SendNewLogMessage("Вкладки привязаны к серверу Bybit: " + serverFullName, LogMessageType.System);
                }

                // как только сервер появился - обновляем список активов с опционами в параметре
                RefreshFuturesSecurityList();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Ограниченное по времени предупреждение об отсутствии сервера Bybit.
        /// </summary>
        private void LogBybitServerNotFound()
        {
            if (DateTime.UtcNow - _lastNoServerLogTime > TimeSpan.FromSeconds(60))
            {
                _lastNoServerLogTime = DateTime.UtcNow;
                SendNewLogMessage("Сервер Bybit не найден. Создайте сервер Bybit в OsTrader и включите у него опцию 'Use Options'.", LogMessageType.System);
            }
        }

        /// <summary>
        /// Периодическая повторная попытка привязки вкладок к серверу Bybit.
        /// Останавливает таймер после успешной привязки.
        /// </summary>
        private void BindTabsToBybitServerRetry()
        {
            try
            {
                if (_tabsBoundToServer)
                {
                    if (_bindRetryTimer != null)
                    {
                        _bindRetryTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        _bindRetryTimer.Dispose();
                        _bindRetryTimer = null;
                    }
                    return;
                }

                BindTabsToBybitServerIfNeeded();
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
        /// Поддерживает подписку вкладки опциона на актуальный контракт:
        /// - если открытые опционы есть - вкладка показывает последний купленный (видна позиция и котировки);
        /// - если позиций нет - вкладка подписывается на целевой опцион для первого входа (с троттлингом).
        /// Переключение на новый опцион для доливки происходит в момент входа (TryEnterStep).
        /// </summary>
        private void UpdateTargetOptionSubscription()
        {
            if (_exitInProgress)
            {
                return; // при выходе вкладкой управляет ExitSellNextStrikeOption
            }

            // пока держим опционы - не уводим вкладку от последнего купленного контракта
            if (_boughtOptions.Count > 0)
            {
                BoughtOption lastBought = _boughtOptions[_boughtOptions.Count - 1];

                if (lastBought.Security != null)
                {
                    BindOptionTabToSecurity(lastBought.Security);
                }
                else if (_tabOptions.Connector.SecurityName != lastBought.SecurityName)
                {
                    _tabOptions.Connector.SecurityName = lastBought.SecurityName;
                }

                return;
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

            // тейк изменился - помечаем, что лимитку нужно перевыставить.
            // Само выставление произойдёт, когда в книге не останется живых ордеров на закрытие
            // (защита от дублирования лимитных тейков).
            _tpDirty = true;
            TryPlaceTakeProfit();

            SendNewLogMessage(
                "Тейк пересчитан: AvgEntry=" + avgEntry.ToString("F2") +
                ", опционы=" + _totalOptionCost.ToString("F2") +
                ", комиссии=" + totalCommissions.ToString("F2") +
                ", объём=" + futuresVolume +
                " => TP=" + _takeProfit.ToString("F2"),
                LogMessageType.System);
        }

        /// <summary>
        /// Есть ли в книге позиции живой ордер на закрытие (None/Pending/Active/Partial).
        /// Живым считается и только что выставленный ордер со статусом None -
        /// иначе робот ставит дубликаты лимитного тейка.
        /// Если задана цена price - ордер считается подходящим только при совпадении цены.
        /// </summary>
        private bool HasLiveCloseOrder(List<Position> longs, decimal price = 0)
        {
            decimal step = _tabFutures.Security != null && _tabFutures.Security.PriceStep > 0
                ? _tabFutures.Security.PriceStep
                : 0.01m;

            for (int i = 0; i < longs.Count; i++)
            {
                for (int i2 = 0; longs[i].CloseOrders != null && i2 < longs[i].CloseOrders.Count; i2++)
                {
                    Order order = longs[i].CloseOrders[i2];

                    if (order == null)
                    {
                        continue;
                    }

                    if (order.State == OrderStateType.None
                        || order.State == OrderStateType.Pending
                        || order.State == OrderStateType.Active
                        || order.State == OrderStateType.Partial)
                    {
                        if (price <= 0)
                        {
                            return true;
                        }

                        if (Math.Abs(order.Price - price) < step)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Снимает все активные ордера на закрытие с позиций (лимитный тейк).
        /// </summary>
        private void CancelAllCloseOrders(List<Position> longs)
        {
            for (int i = 0; i < longs.Count; i++)
            {
                _tabFutures.CloseAllOrderToPosition(longs[i], "TakeProfitCancel");
            }
        }

        /// <summary>
        /// Выставляет лимитный тейк-ордер, но ТОЛЬКО если в книге нет живых ордеров на закрытие.
        /// Иначе остаётся флаг _tpDirty, и выставление повторится на следующем тике
        /// после подтверждения отмены старой лимитки. Это исключает дубли и переворот позиции.
        /// </summary>
        private void TryPlaceTakeProfit()
        {
            try
            {
                if (_takeProfit <= 0 || _exitInProgress)
                {
                    _tpDirty = false;
                    return;
                }

                // пока опцион текущего шага не исполнился, затраты на опционы неполные:
                // тейк = цене входа и лимитка мгновенно исполнится. Ждём заполнения опциона.
                if (_optionBuysPending > 0)
                {
                    return; // _tpDirty остаётся true, повторим после заполнения опциона
                }

                List<Position> openPositions = _tabFutures.PositionsOpenAll;
                if (openPositions == null)
                {
                    return;
                }

                List<Position> longs = openPositions
                    .Where(p => p != null && p.Direction == Side.Buy && p.OpenVolume > 0)
                    .ToList();

                if (longs.Count == 0)
                {
                    _tpDirty = false;
                    return;
                }

                Security sec = _tabFutures.Security;
                decimal price = _takeProfit;

                // округление до шага цены инструмента
                if (sec != null && sec.PriceStep > 0)
                {
                    price = Math.Round(price / sec.PriceStep) * sec.PriceStep;
                }

                // ограничения биржи по цене
                if (sec != null && sec.PriceLimitHigh > 0 && price > sec.PriceLimitHigh)
                {
                    price = sec.PriceLimitHigh;
                }
                if (sec != null && sec.PriceLimitLow > 0 && price < sec.PriceLimitLow)
                {
                    price = sec.PriceLimitLow;
                }

                // если живой ордер на закрытие уже стоит по нужной цене - не трогаем.
                // Это исключает лишние отмены тейк-ордера после перезапуска,
                // из-за которых OsEngine помечает позицию как ClosingFail.
                if (HasLiveCloseOrder(longs, price))
                {
                    _tpDirty = false;
                    return;
                }

                // снимаем старые лимитки
                CancelAllCloseOrders(longs);

                // если после снятия ещё остались живые ордера (асинхронная отмена) - ждём подтверждения
                if (HasLiveCloseOrder(longs))
                {
                    return; // _tpDirty остаётся true, повторим на следующем тике
                }

                decimal totalVolume = 0;

                for (int i = 0; i < longs.Count; i++)
                {
                    _tabFutures.CloseAtLimit(longs[i], price, longs[i].OpenVolume, "TakeProfitLimit");
                    totalVolume += longs[i].OpenVolume;
                }

                _tpDirty = false;

                SendNewLogMessage(
                    "Тейк-лимит выставлен: " + price.ToString("F2") + ", объём " + totalVolume,
                    LogMessageType.System);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Самовосстановление тейк-лимита на каждом тике:
        /// - если тейк "грязный" и книга чиста - выставляет ордер;
        /// - если лимитка пропала (отменена/исполнена) - помечает тейк "грязным".
        /// </summary>
        private void EnsureTakeProfitOrder()
        {
            try
            {
                if (_takeProfit <= 0 || _exitInProgress)
                {
                    return;
                }

                List<Position> openPositions = _tabFutures.PositionsOpenAll;
                if (openPositions == null)
                {
                    return;
                }

                List<Position> longs = openPositions
                    .Where(p => p != null && p.Direction == Side.Buy && p.OpenVolume > 0)
                    .ToList();

                if (longs.Count == 0)
                {
                    return;
                }

                // лимитки нет, а должна быть - помечаем "грязной"
                if (!_tpDirty && !HasLiveCloseOrder(longs))
                {
                    _tpDirty = true;
                }

                if (_tpDirty)
                {
                    TryPlaceTakeProfit();
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Проверка, что в журнале фьючерсной вкладки ровно одна открытая запись лонг-позиции.
        /// Дубли записей (часто после перезапуска с открытой позицией) - аномалия,
        /// при которой возможна двойная продажа одной позиции и переворот в шорт.
        /// </summary>
        private bool HasDuplicateFuturesPositions()
        {
            List<Position> openPositions = _tabFutures.PositionsOpenAll;
            if (openPositions == null)
            {
                return false;
            }

            int longsCount = 0;

            for (int i = 0; i < openPositions.Count; i++)
            {
                if (openPositions[i] != null
                    && openPositions[i].Direction == Side.Buy
                    && openPositions[i].OpenVolume > 0)
                {
                    longsCount++;
                }
            }

            return longsCount > 1;
        }

        /// <summary>
        /// Ограниченное по времени предупреждение о дублях позиций (робот при этом приостановлен).
        /// </summary>
        private void LogDuplicatePositionsStop()
        {
            if (DateTime.UtcNow - _lastDupLogTime > TimeSpan.FromSeconds(60))
            {
                _lastDupLogTime = DateTime.UtcNow;
                SendNewLogMessage(
                    "В журнале несколько открытых записей лонг-позиции по фьючерсу (вероятно, дубли после перезапуска). " +
                    "Робот приостановлен. Закройте позиции на бирже и пересоздайте робота, чтобы очистить журнал.",
                    LogMessageType.Error);
            }
        }

        /// <summary>
        /// Восстановление состояния после перезапуска: подхватывает уже открытые позиции
        /// из журналов вкладок (фьючерс + купленные опционы), пересчитывает тейк
        /// и выставляет лимитный тейк-ордер.
        /// </summary>
        private void TryRestoreState()
        {
            try
            {
                if (StartProgram != StartProgram.IsOsTrader)
                {
                    _stateRestored = true;
                    return; // в тестере/оптимизаторе робот всегда стартует с нуля
                }

                // шортовая позиция - аномалия: не восстанавливаем, робот приостановится (см. тиковый цикл)
                if (_tabFutures.VolumeNet < 0)
                {
                    _stateRestored = true;
                    SendNewLogMessage(
                        "При восстановлении обнаружена шортовая позиция (объём " + _tabFutures.VolumeNet +
                        "). Робот приостановлен. Закройте шорт вручную.",
                        LogMessageType.Error);
                    return;
                }

                // дубли записей позиции в журнале - аномалия, восстанавливаться нельзя
                if (HasDuplicateFuturesPositions())
                {
                    _stateRestored = true;
                    LogDuplicatePositionsStop();
                    return;
                }

                // открытой фьючерсной позиции нет - восстанавливать нечего
                if (_tabFutures.VolumeNet == 0)
                {
                    _stateRestored = true;
                    return;
                }

                // --- восстанавливаем купленные опционы из журнала вкладки опциона ---
                List<Position> optionPositions = _tabOptions.PositionsOpenAll;

                if (optionPositions != null)
                {
                    for (int i = 0; i < optionPositions.Count; i++)
                    {
                        Position pos = optionPositions[i];

                        if (pos == null || pos.Direction != Side.Buy || pos.OpenVolume <= 0)
                        {
                            continue;
                        }

                        if (_boughtOptions.Any(o => o.SecurityName == pos.SecurityName))
                        {
                            continue; // уже учтён
                        }

                        decimal cost = pos.EntryPrice * pos.OpenVolume;

                        _boughtOptions.Add(new BoughtOption
                        {
                            SecurityName = pos.SecurityName,
                            Security = FindSecurityByName(pos.SecurityName),
                            Position = pos,
                            Cost = cost
                        });

                        _totalOptionCost += cost;
                        _optionsBuyCommission += CalcCommission(_tabOptions, pos.EntryPrice, pos.OpenVolume);
                        _stepsDone++;
                    }
                }

                // --- точка отсчёта для доливки = средняя цена входа ---
                _lastFuturesFillPrice = GetFuturesAverageEntry();

                if (_stepsDone <= 0)
                {
                    _stepsDone = 1; // минимум один шаг (первый вход) уже был
                }

                _stateRestored = true;

                SendNewLogMessage(
                    "Состояние восстановлено после перезапуска: фьючерс " + _tabFutures.VolumeNet +
                    ", опционов в списке " + _boughtOptions.Count,
                    LogMessageType.System);

                // пересчёт тейка выставит лимитный тейк-ордер на существующую позицию
                RecalcTakeProfit();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Поиск инструмента по имени в списке инструментов сервера (для восстановления состояния).
        /// </summary>
        private Security FindSecurityByName(string name)
        {
            try
            {
                IServer server = _tabOptions.Connector.MyServer;
                if (server == null || server.Securities == null)
                {
                    return null;
                }

                return server.Securities.FirstOrDefault(s => s != null && s.Name == name);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return null;
            }
        }

        // ===================== Вход в позицию =====================

        /// <summary>
        /// Шаг входа (первый вход и доливка): СНАЧАЛА покупается опцион Put рыночным ордером.
        /// Фьючерс покупается отдельно - по событию заполнения опциона (BuyFuturesForStep),
        /// чтобы затраты на опционы были известны до расчёта и выставления тейк-лимита.
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

                // после неудачной попытки (не было котировки) не дёргаем вкладку
                // и не спамим лог - повторная попытка через 30 секунд
                if (DateTime.UtcNow - _lastEntryGiveUpTime < TimeSpan.FromSeconds(30))
                {
                    return;
                }

                BindOptionTabToSecurity(target);
            }

            // --- проверки готовности вкладки опциона ---
            if (_tabOptions.Security == null)
            {
                SendNewLogMessage("Опцион ещё не загружен на вкладку. Ждём инструменты Bybit.", LogMessageType.System);
                return;
            }

            if (_tabOptions.PriceBestAsk <= 0)
            {
                _lastEntryGiveUpTime = DateTime.UtcNow;
                SendNewLogMessage("Нет котировки по опциону " + _tabOptions.Security.Name + ". Попытка входа отложена на 30 секунд.", LogMessageType.System);
                return;
            }

            // --- объём опциона с округлением под шаг инструмента ---
            decimal optionVol = RoundVolume(GetOptionVolumeForStep(), _tabOptions.Security);

            if (optionVol <= 0)
            {
                SendNewLogMessage("Объём опциона меньше минимального торгового объёма. Шаг пропущен.", LogMessageType.Error);
                return;
            }

            // --- покупаем опцион рыночным ордером (фьючерс докупится после заполнения) ---
            _optionBuysPending++;
            Position optionPos = _tabOptions.BuyAtMarket(optionVol, signalType);

            if (optionPos == null)
            {
                _optionBuysPending--;
                SendNewLogMessage("Не удалось выставить покупку опциона. Шаг пропущен.", LogMessageType.Error);
                return;
            }

            SendNewLogMessage(
                signalType + ": выставлена покупка опциона " + optionVol + " " + _tabOptions.Security.Name,
                LogMessageType.System);
        }

        /// <summary>
        /// Покупка фьючерса рыночным ордером. Вызывается ПОСЛЕ заполнения опциона,
        /// чтобы тейк-лимит всегда считался с учётом фактической цены опциона.
        /// </summary>
        private void BuyFuturesForStep(string signalType)
        {
            try
            {
                if (_exitInProgress)
                {
                    return; // выход уже идёт - фьючерс не докупаем
                }

                if (_tabFutures.Security == null)
                {
                    SendNewLogMessage("Фьючерс не загружен (" + _futuresSecurityName.ValueString + "). Фьючерс не куплен.", LogMessageType.Error);
                    return;
                }

                decimal futuresVol = RoundVolume(GetFuturesVolumeForStep(), _tabFutures.Security);

                if (futuresVol <= 0)
                {
                    SendNewLogMessage("Объём фьючерса меньше минимального торгового объёма. Фьючерс не куплен.", LogMessageType.Error);
                    return;
                }

                _futuresBuysPending++;
                Position futuresPos = _tabFutures.BuyAtMarket(futuresVol, signalType);

                if (futuresPos == null)
                {
                    _futuresBuysPending--;
                    SendNewLogMessage("Не удалось выставить покупку фьючерса.", LogMessageType.Error);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Объём фьючерса для текущего шага (первый вход или доливка).
        /// Fixed - фиксированный объём FuturesVolumePerStep.
        /// Multiplier - объём, равный текущему суммарному объёму позиции, умноженному на множитель
        /// (первый вход - базовый объём).
        /// </summary>
        private decimal GetFuturesVolumeForStep()
        {
            if (_volumeGrowthMode.ValueString == "Multiplier")
            {
                decimal current = _tabFutures.VolumeNet;

                if (current > 0)
                {
                    decimal mult = _volumeMultiplier.ValueDecimal > 0 ? _volumeMultiplier.ValueDecimal : 1;
                    return current * mult;
                }
            }

            return _futuresVolumePerStep.ValueDecimal;
        }

        /// <summary>
        /// Объём опционов для текущего шага (первый вход или доливка).
        /// Fixed - фиксированный объём OptionLotsPerStep.
        /// Multiplier - объём, равный суммарному объёму купленных опционов, умноженному на множитель
        /// (первый вход - базовый объём).
        /// </summary>
        private decimal GetOptionVolumeForStep()
        {
            if (_volumeGrowthMode.ValueString == "Multiplier")
            {
                decimal current = 0;

                for (int i = 0; i < _boughtOptions.Count; i++)
                {
                    if (_boughtOptions[i].Position != null)
                    {
                        current += _boughtOptions[i].Position.OpenVolume;
                    }
                }

                if (current > 0)
                {
                    decimal mult = _volumeMultiplier.ValueDecimal > 0 ? _volumeMultiplier.ValueDecimal : 1;
                    return current * mult;
                }
            }

            return _optionLotsPerStep.ValueDecimal;
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

        // ===================== Выход по тейку (продажа опциона следующего страйка) =====================

        /// <summary>
        /// Старт выхода: продаётся опцион Put на страйк выше первого купленного
        /// (например, куплен 76 -> продаётся 77) рыночным ордером, объёмом,
        /// равным суммарному объёму всех купленных опционов.
        /// Купленные опционы при этом не продаются - они остаются на бирже
        /// (ими распоряжается пользователь или они истекают).
        /// Вызывается, когда фьючерсная позиция закрыта (сработал тейк-лимит).
        /// </summary>
        private void TryExitByTakeProfit()
        {
            if (_exitInProgress)
            {
                return;
            }

            if (_boughtOptions.Count == 0)
            {
                // опционов нет - только фьючерс, он уже закрыт. Завершаем цикл.
                MaybeFinalizeCycle();
                return;
            }

            _exitInProgress = true;
            _optionSellPending = false;

            ExitSellNextStrikeOption();
        }

        /// <summary>
        /// Продаёт опцион следующего страйка (от первого купленного) на сумму всех купленных.
        /// Перед продажей переключает вкладку опциона на продаваемый контракт,
        /// чтобы получить свежую котировку (рыночный ордер требует BestBid).
        /// </summary>
        private void ExitSellNextStrikeOption()
        {
            if (_optionSellPending)
            {
                return; // ждём исполнения продажи
            }

            if (_boughtOptions.Count == 0)
            {
                MaybeFinalizeCycle();
                return;
            }

            BoughtOption first = _boughtOptions[0];
            Security firstSecurity = first != null && first.Security != null
                ? first.Security
                : FindSecurityByName(first != null ? first.SecurityName : null);

            if (firstSecurity == null)
            {
                if (DateTime.UtcNow - _lastExitQuoteLogTime > TimeSpan.FromSeconds(30))
                {
                    _lastExitQuoteLogTime = DateTime.UtcNow;
                    SendNewLogMessage("Не найден инструмент первого купленного опциона. Закройте позиции вручную.", LogMessageType.Error);
                }
                return;
            }

            Security target = GetExitSellOption(firstSecurity);

            if (target == null)
            {
                if (DateTime.UtcNow - _lastExitQuoteLogTime > TimeSpan.FromSeconds(30))
                {
                    _lastExitQuoteLogTime = DateTime.UtcNow;
                    SendNewLogMessage(
                        "Не найден опцион Put на страйк " + (firstSecurity.Strike + _strikeStep.ValueDecimal) +
                        " (следующий от купленного " + firstSecurity.Strike + "). Закройте позиции вручную.",
                        LogMessageType.Error);
                }
                return;
            }

            // подписываем вкладку на продаваемый контракт
            BindOptionTabToSecurity(target);

            if (_tabOptions.PriceBestBid <= 0)
            {
                if (DateTime.UtcNow - _lastExitQuoteLogTime > TimeSpan.FromSeconds(30))
                {
                    _lastExitQuoteLogTime = DateTime.UtcNow;
                    SendNewLogMessage("Ждём котировку по опциону " + target.Name + " для закрытия.", LogMessageType.System);
                }
                return; // тик придёт после переподписки - выход продолжит Options_NewTickEvent
            }

            // объём = суммарный объём всех купленных опционов
            decimal totalQty = 0;

            for (int i = 0; i < _boughtOptions.Count; i++)
            {
                if (_boughtOptions[i].Position != null)
                {
                    totalQty += _boughtOptions[i].Position.OpenVolume;
                }
            }

            totalQty = RoundVolume(totalQty, target);

            if (totalQty <= 0)
            {
                SendNewLogMessage("Объём продажи опциона меньше минимального торгового объёма.", LogMessageType.Error);
                return;
            }

            _optionSellPending = true;
            _tabOptions.SellAtMarket(totalQty, "TakeProfit");

            SendNewLogMessage(
                "Продажа опциона " + target.Name + " (страйк " + target.Strike +
                ", объём " + totalQty + ", купленных в списке " + _boughtOptions.Count + ")",
                LogMessageType.System);
        }

        /// <summary>
        /// Целевой опцион для продажи при выходе: Put на страйк выше первого купленного,
        /// экспирация - ближайшая к экспирации первого купленного.
        /// </summary>
        private Security GetExitSellOption(Security firstBought)
        {
            try
            {
                IServer server = _tabOptions.Connector.MyServer;
                if (server == null || server.Securities == null)
                {
                    return null;
                }

                decimal sellStrike = firstBought.Strike + _strikeStep.ValueDecimal;

                List<Security> candidates = server.Securities
                    .Where(s => s != null
                        && s.SecurityType == SecurityType.Option
                        && s.OptionType == OptionType.Put
                        && s.NameClass == firstBought.NameClass
                        && s.Strike == sellStrike)
                    .ToList();

                if (candidates.Count == 0)
                {
                    return null;
                }

                candidates.Sort((a, b) =>
                    (a.Expiration - firstBought.Expiration).Duration()
                    .CompareTo((b.Expiration - firstBought.Expiration).Duration()));

                return candidates[0];
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return null;
            }
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
            _tpDirty = false;
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

                    // после перезапуска восстанавливаем состояние по открытым позициям
                    if (!_stateRestored)
                    {
                        TryRestoreState();
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

                    // аварийная защита: шортовая позиция - аномалия, робот её не трогает.
                    // Шорт нужно закрыть вручную (в т.ч. на бирже), после этого робот продолжит.
                    if (_tabFutures.VolumeNet < 0)
                    {
                        if (DateTime.UtcNow - _lastShortLogTime > TimeSpan.FromSeconds(60))
                        {
                            _lastShortLogTime = DateTime.UtcNow;
                            SendNewLogMessage(
                                "Обнаружена шортовая позиция по фьючерсу (объём " + _tabFutures.VolumeNet +
                                "). Робот приостановлен. Закройте шорт вручную, затем включите режим заново.",
                                LogMessageType.Error);
                        }
                        return;
                    }

                    // аварийная защита: дубли записей позиции в журнале - робот приостанавливается,
                    // чтобы не допустить двойного закрытия и переворота в шорт
                    if (HasDuplicateFuturesPositions())
                    {
                        LogDuplicatePositionsStop();
                        return;
                    }

                    // 1) держим подписку на актуальный целевой опцион
                    if (_useDynamicOptionSelection.ValueBool)
                    {
                        UpdateTargetOptionSubscription();
                    }

                    bool noFuturesPosition = _tabFutures.VolumeNet == 0;

                    // 2) первый вход: позиции нет, покупки не висят, все опционы распроданы.
                    //    _boughtOptions.Count == 0 обязателен: если фьючерс уже закрылся на бирже,
                    //    но выход по опционам ещё не начался, входы не делаем,
                    //    иначе два куска логики будут драться за вкладку опциона.
                    if (noFuturesPosition
                        && _futuresBuysPending <= 0
                        && _optionBuysPending <= 0
                        && _boughtOptions.Count == 0)
                    {
                        TryEnterStep("FirstEntry");
                        return;
                    }

                    // 3) контроль тейк-лимита: лимитный ордер должен висеть на позиции.
                    //    Если он пропал/отклонён - перевыставляем (самовосстановление).
                    if (_takeProfit > 0 && !noFuturesPosition && _futuresBuysPending <= 0)
                    {
                        EnsureTakeProfitOrder();
                    }

                    // 4) доливка: цена упала на PriceDropStep от последнего входа
                    if (_lastFuturesFillPrice > 0
                        && price <= _lastFuturesFillPrice - _priceDropStep.ValueDecimal
                        && !noFuturesPosition
                        && _futuresBuysPending <= 0
                        && _optionBuysPending <= 0
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
        /// Фьючерсная позиция помечена как закрытая с ошибкой (обычно при отмене
        /// старого тейк-ордера после перезапуска). Позиция на бирже при этом не страдает -
        /// просто фиксируем факт и продолжаем.
        /// </summary>
        private void Futures_PositionClosingFailEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    string positionInfo = position != null
                        ? "№" + position.Number + " (" + position.SecurityName + ")"
                        : "неизвестная";

                    SendNewLogMessage(
                        "Фьючерсная позиция " + positionInfo + " помечена как закрытая с ошибкой. " +
                        "Обычно это происходит при отмене старого тейк-ордера после перезапуска - " +
                        "позиция на бирже не пострадала, робот продолжает работу.",
                        LogMessageType.System);
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
        /// Фьючерсная позиция открыта (первый вход или доливка): пересчитываем тейк и выставляем лимитный тейк-ордер.
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

                    if (_exitInProgress)
                    {
                        // выход уже идёт (тейк сработал), а доливочный объём исполнился позже - закрываем остаток по рынку
                        SendNewLogMessage("Остаток объёма после тейка закрывается по рынку.", LogMessageType.System);
                        _tabFutures.CloseAllAtMarket("TakeProfitLeftover");
                        return;
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
        /// Фьючерсная позиция закрыта (сработал лимитный тейк или иное закрытие):
        /// фиксируем комиссию выхода; если есть открытые опционы - продаём их по FIFO.
        /// </summary>
        private void Futures_PositionClosingSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    _futuresCloseCommission += CalcCommissionOnCloseOrders(_tabFutures, position);

                    // позиция по фьючерсу закрыта - выходим из опционов (FIFO, рыночными ордерами)
                    if (_boughtOptions.Count > 0 && !_exitInProgress)
                    {
                        TryExitByTakeProfit();
                        return;
                    }

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
        /// Опцион куплен (вход) или продажа опциона следующего страйка исполнилась (выход).
        /// </summary>
        private void Options_PositionOpeningSuccesEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    // продажа опциона следующего страйка при выходе по тейку: шорт открыт - цикл завершён.
                    // Купленные опционы остаются на бирже (ими распоряжается пользователь или они истекают).
                    if (_exitInProgress && position != null && position.Direction == Side.Sell)
                    {
                        _optionSellPending = false;
                        SendNewLogMessage(
                            "Опцион закрытия продан: " + position.SecurityName + ". Цикл завершён.",
                            LogMessageType.System);
                        ResetCycle();
                        return;
                    }

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

                    // опцион куплен - теперь покупаем фьючерс (если не идёт выход)
                    if (!_exitInProgress)
                    {
                        string stepSignal = string.IsNullOrEmpty(position.SignalTypeOpen) ? "AddStep" : position.SignalTypeOpen;
                        BuyFuturesForStep(stepSignal);
                    }

                    RecalcTakeProfit();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Опцион не исполнился (отклонён биржей).
        /// Если это была продажа опциона следующего страйка при выходе - снимаем флаг ожидания,
        /// попытка повторится на следующем тике.
        /// </summary>
        private void Options_PositionOpeningFailEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    if (_optionBuysPending > 0)
                    {
                        _optionBuysPending--;
                    }

                    // продажа при выходе не исполнилась - пробуем снова
                    if (_exitInProgress && _optionSellPending)
                    {
                        _optionSellPending = false;
                        SendNewLogMessage(
                            "Продажа опциона закрытия не исполнилась: " +
                            (position != null ? position.SecurityName : "неизвестно") +
                            ". Повторим попытку.",
                            LogMessageType.Error);
                        return;
                    }

                    SendNewLogMessage(
                        "Опцион не исполнился: " + (position != null ? position.SecurityName : "неизвестно") +
                        ". Шаг пропущен, фьючерс не покупается.",
                        LogMessageType.Error);
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Фьючерс не исполнился. Если это был первый вход (фьючерсной позиции нет вообще) -
        /// продаём купленные опционы обратно, чтобы не остаться без хеджа.
        /// </summary>
        private void Futures_PositionOpeningFailEvent(Position position)
        {
            lock (_locker)
            {
                try
                {
                    if (_futuresBuysPending > 0)
                    {
                        _futuresBuysPending--;
                    }

                    SendNewLogMessage(
                        "Фьючерс не исполнился: " + (position != null ? position.SecurityName : "неизвестно"),
                        LogMessageType.Error);

                    if (_tabFutures.VolumeNet <= 0 && _boughtOptions.Count > 0 && !_exitInProgress)
                    {
                        SendNewLogMessage("Первый вход не состоялся. Продаём купленные опционы обратно.", LogMessageType.System);

                        for (int i = 0; i < _boughtOptions.Count; i++)
                        {
                            BoughtOption option = _boughtOptions[i];

                            if (option.Position != null && option.Position.OpenVolume > 0)
                            {
                                if (_tabOptions.PriceBestBid > 0)
                                {
                                    _tabOptions.CloseAtMarket(option.Position, option.Position.OpenVolume, "EntryRollback");
                                }
                                else
                                {
                                    SendNewLogMessage("Нет котировки для отката опциона " + option.SecurityName + ". Закройте его вручную.", LogMessageType.Error);
                                }
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Опцион закрыт (вручную или откатом входа): убираем из списка.
        /// При выходе продаётся опцион следующего страйка, а не купленные - их закрытие
        /// обрабатывается в Options_PositionOpeningSuccesEvent (шорт по 77P).
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
                        return; // выход завершается по факту продажи опциона следующего страйка
                    }

                    MaybeFinalizeCycle();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
        /// Тики опционной вкладки: продвигают выход (продажа опциона следующего страйка).
        /// </summary>
        private void Options_NewTickEvent(Trade trade)
        {
            lock (_locker)
            {
                try
                {
                    if (_exitInProgress && !_optionSellPending && _boughtOptions.Count > 0)
                    {
                        ExitSellNextStrikeOption();
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
        public override bool HasCustomSettingsDialog => true;

        public override void ShowIndividualSettingsDialog()
        {
            try
            {
                SolanaParametersUi ui = new SolanaParametersUi(this, GetParametersForUi());
                ui.Show();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        // ===================== Служебное для окна параметров =====================

        /// <summary>
        /// Параметры робота для окна настройки (по имени).
        /// </summary>
        public Dictionary<string, IIStrategyParameter> GetParametersForUi()
        {
            return new Dictionary<string, IIStrategyParameter>
            {
                ["Regime"] = _regime,
                ["FuturesSecurityName"] = _futuresSecurityName,
                ["OptionBaseAsset"] = _optionBaseAsset,
                ["CentralStrikeOverride"] = _centralStrikeOverride,
                ["StrikeStep"] = _strikeStep,
                ["OptionMinDaysToExpiry"] = _optionMinDaysToExpiry,
                ["OptionMaxDaysToExpiry"] = _optionMaxDaysToExpiry,
                ["FuturesVolumePerStep"] = _futuresVolumePerStep,
                ["OptionLotsPerStep"] = _optionLotsPerStep,
                ["PriceDropStep"] = _priceDropStep,
                ["MaxSteps"] = _maxSteps,
                ["VolumeGrowthMode"] = _volumeGrowthMode,
                ["VolumeMultiplier"] = _volumeMultiplier,
                ["UseDynamicOptionSelection"] = _useDynamicOptionSelection
            };
        }

        /// <summary>
        /// Обновляет список доступных фьючерсов (базовых активов, у которых есть опционы)
        /// в параметре FuturesSecurityName и возвращает актуальный список.
        /// </summary>
        public List<string> RefreshFuturesSecurityList()
        {
            List<string> names = new List<string>();

            try
            {
                List<string> baseAssets = GetOptionBaseAssets();

                for (int i = 0; i < baseAssets.Count; i++)
                {
                    names.Add(baseAssets[i] + "USDT.P");
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }

            // текущее значение всегда оставляем в списке (даже если оно задано вручную)
            if (names.Contains(_futuresSecurityName.ValueString) == false)
            {
                names.Insert(0, _futuresSecurityName.ValueString);
            }

            _futuresSecurityName.ValuesString = names;

            return names;
        }

        /// <summary>
        /// Базовые активы, по которым на сервере есть опционы (SOL, BTC, ETH и т.п.).
        /// Если сервер ещё не подключён - возвращает стандартный список.
        /// </summary>
        private List<string> GetOptionBaseAssets()
        {
            List<string> result = new List<string>();

            try
            {
                IServer server = _tabFutures != null ? _tabFutures.Connector.MyServer : null;

                if (server != null && server.Securities != null)
                {
                    for (int i = 0; i < server.Securities.Count; i++)
                    {
                        Security sec = server.Securities[i];

                        if (sec == null || sec.SecurityType != SecurityType.Option || string.IsNullOrEmpty(sec.Name))
                        {
                            continue;
                        }

                        int idx = sec.Name.IndexOf('-');

                        if (idx > 0)
                        {
                            string baseAsset = sec.Name.Substring(0, idx);

                            if (result.Contains(baseAsset) == false)
                            {
                                result.Add(baseAsset);
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }

            if (result.Count == 0)
            {
                result.Add("SOL");
                result.Add("BTC");
                result.Add("ETH");
            }

            return result;
        }

        /// <summary>
        /// Вызывается окном параметров после применения значений:
        /// синхронизирует базовый актив опционов с выбранным фьючерсом
        /// и переключает вкладку фьючерса на новый инструмент (если нет открытой позиции).
        /// </summary>
        public void OnParametersAppliedByUi()
        {
            try
            {
                string futuresName = _futuresSecurityName.ValueString;

                // базовый актив из имени фьючерса (SOLUSDT.P -> SOL)
                if (string.IsNullOrEmpty(futuresName) == false)
                {
                    string baseAsset = futuresName.Replace("USDT.P", "").Replace(".P", "");

                    if (string.IsNullOrEmpty(baseAsset) == false && _optionBaseAsset.ValueString != baseAsset)
                    {
                        _optionBaseAsset.ValueString = baseAsset;
                        SendNewLogMessage("Базовый актив опционов установлен: " + baseAsset, LogMessageType.System);
                    }
                }

                // переключение фьючерсной вкладки на новый инструмент
                if (_tabFutures.Connector.SecurityName != futuresName)
                {
                    if (_tabFutures.VolumeNet != 0)
                    {
                        SendNewLogMessage("Нельзя сменить фьючерс при открытой позиции. Сначала закройте позицию.", LogMessageType.Error);
                        _futuresSecurityName.ValueString = _tabFutures.Connector.SecurityName;
                        return;
                    }

                    _tabFutures.Connector.SecurityName = futuresName;
                    SendNewLogMessage("Вкладка фьючерса переключена на " + futuresName, LogMessageType.System);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }
    }
}
