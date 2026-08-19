/*
 * Красивое окно параметров робота SolanaPutOptionsHedgedRobot.
 * Открывается кнопкой "Parameters" в панели управления роботом.
 * Особенности:
 * - компактная компоновка в две колонки;
 * - режим робота - тумблер с бегунком и надписью ON/OFF;
 * - выпадающие списки для всех числовых параметров (можно вводить своё значение);
 * - подсказки - во всплывающем окне у значка "?";
 * - список фьючерсов формируется из базовых активов, у которых есть опционы.
 */

using OsEngine.Entity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace OsEngine.Robots.SolanaOptions
{
    public class SolanaParametersUi : Window
    {
        private readonly SolanaPutOptionsHedgedRobot _robot;
        private readonly Dictionary<string, IIStrategyParameter> _parameters;

        private Border _regimePill;
        private Border _regimeKnob;
        private TextBlock _regimeText;

        private ComboBox _futuresSecurityCombo;
        private ComboBox _optionBaseCombo;
        private ComboBox _centralStrikeCombo;
        private ComboBox _stepCombo;
        private ComboBox _minDaysCombo;
        private ComboBox _futuresVolumeCombo;
        private ComboBox _optionLotsCombo;
        private ComboBox _maxStepsCombo;
        private ComboBox _dynamicSelectionCombo;

        private int _sectionRows;

        private readonly Brush _bg = new SolidColorBrush(Color.FromRgb(40, 44, 52));
        private readonly Brush _bgControl = new SolidColorBrush(Color.FromRgb(28, 32, 40));
        private readonly Brush _textMain = new SolidColorBrush(Color.FromRgb(235, 235, 235));
        private readonly Brush _textDim = new SolidColorBrush(Color.FromRgb(171, 178, 191));
        private readonly Brush _accent = new SolidColorBrush(Color.FromRgb(97, 175, 239));
        private readonly Brush _green = new SolidColorBrush(Color.FromRgb(90, 180, 120));
        private readonly Brush _gray = new SolidColorBrush(Color.FromRgb(110, 115, 125));
        private readonly Brush _header = new SolidColorBrush(Color.FromRgb(224, 108, 117));

        public SolanaParametersUi(SolanaPutOptionsHedgedRobot robot, Dictionary<string, IIStrategyParameter> parameters)
        {
            _robot = robot;
            _parameters = parameters;

            Title = "Параметры робота / " + robot.NameStrategyUniq;
            Width = 780;
            Height = 700;
            MinWidth = 640;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = _bg;

            BuildUi();
            LoadValues();
        }

        // ===================== Построение интерфейса =====================

        private void BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // заголовок
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // подзаголовок
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // содержимое
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // кнопки

            // --- заголовок ---
            TextBlock title = new TextBlock
            {
                Text = "SolanaPutOptionsHedgedRobot",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = _accent,
                Margin = new Thickness(20, 14, 20, 0)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            TextBlock subtitle = new TextBlock
            {
                Text = "Опционы Put SOL + хедж фьючерсом. Доливка при падении цены, единый средний тейк-лимит. " +
                       "Наведите на ? для подсказки.",
                FontSize = 12,
                Foreground = _textDim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 2, 20, 10)
            };
            Grid.SetRow(subtitle, 1);
            root.Children.Add(subtitle);

            // --- содержимое: две колонки ---
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(20, 0, 20, 0)
            };

            StackPanel content = new StackPanel();
            scroll.Content = content;

            Grid sectionGrid;

            AddSectionHeader(content, "Режим работы", out sectionGrid);
            AddParam(sectionGrid, "Regime", "Режим работы робота: ON - торговать, OFF - выключен. " +
                "Остальные параметры применяются только при ON.", CreateRegimeToggle());

            AddSectionHeader(content, "Инструменты", out sectionGrid);
            _futuresSecurityCombo = CreateEditableCombo();
            Button refreshButton = CreateSmallButton("Обновить", RefreshFuturesList);
            refreshButton.ToolTip = "Обновить список фьючерсов с сервера (активы, у которых есть опционы).";
            AddParam(sectionGrid, "FuturesSecurityName",
                "Фьючерс базового актива. Список формируется из активов, у которых есть опционы на бирже. " +
                "Можно ввести имя вручную (например, SOLUSDT.P).",
                _futuresSecurityCombo,
                refreshButton);

            _optionBaseCombo = CreateEditableCombo("SOL", "BTC", "ETH");
            AddParam(sectionGrid, "OptionBaseAsset",
                "Базовый актив опционов (подставляется в имя опционного контракта). " +
                "Автоматически берётся из имени фьючерса (SOLUSDT.P -> SOL).",
                _optionBaseCombo);

            AddSectionHeader(content, "Опционы", out sectionGrid);
            _centralStrikeCombo = CreateEditableCombo(
                "CS", "CS-1", "CS-2", "CS-3", "CS-4", "CS-5",
                "CS-6", "CS-7", "CS-8", "CS-9", "CS-10");
            AddParam(sectionGrid, "CentralStrikeOverride",
                "Страйк первой покупки относительно центрального (ближайший к текущей цене): " +
                "CS - центральный страйк, CS-1 - на один страйк ниже, CS-2 - на два ниже и т.д. " +
                "Каждая доливка покупает опцион ещё на один страйк ниже (лестница вниз).",
                _centralStrikeCombo);

            _minDaysCombo = CreateEditableCombo("0D", "1D", "2D", "2D+", "2D++", "2D+++");
            AddParam(sectionGrid, "OptionMinDaysToExpiry",
                "Экспирация опциона из списка дат экспирации биржи: " +
                "0D - истекает в текущие сутки (до экспирации меньше 24 часов), " +
                "1D - через 1 день (24-48 часов), " +
                "2D - через 2 дня, " +
                "2D+ - следующая дата экспирации на бирже после 2D, " +
                "2D++ - после 2D+, 2D+++ - после 2D++.",
                _minDaysCombo);

            AddSectionHeader(content, "Доливка", out sectionGrid);
            _stepCombo = CreateEditableCombo(
                "0.01", "0.02", "0.03", "0.04", "0.05", "0.06", "0.07", "0.08", "0.09", "0.1",
                "0.15", "0.2", "0.25", "0.3", "0.4", "0.5", "0.75", "1", "1.5", "2", "2.5", "3", "4", "5");
            AddParam(sectionGrid, "StrikeStep",
                "Шаг доливки в USDT: когда цена фьючерса упадёт на это значение от последнего входа, " +
                "робот докупит опцион и фьючерс. Список кратен шагу цены инструмента (для SOL 0.01), " +
                "можно ввести своё значение.",
                _stepCombo);

            _maxStepsCombo = CreateEditableCombo("1", "2", "3", "4", "5", "6", "8", "10");
            AddParam(sectionGrid, "MaxSteps",
                "Максимальное число шагов за цикл: первый вход + доливки. " +
                "При достижении лимита доливки прекращаются.",
                _maxStepsCombo);

            AddSectionHeader(content, "Объёмы", out sectionGrid);
            _futuresVolumeCombo = CreateEditableCombo("0.1", "0.5", "1", "2", "5", "10");
            AddParam(sectionGrid, "FuturesVolumePerStep",
                "Начальный объём (количество) опциона и фьючерса, с которого начинается цикл робота. " +
                "Должен быть равен максимальному из двух минимальных объёмов: минимального объёма фьючерса " +
                "и минимального объёма опциона в сделке (робот сам поднимет объём до этого минимума).",
                _futuresVolumeCombo);

            _optionLotsCombo = CreateEditableCombo("1", "1.5", "2", "2.5", "3", "4", "5");
            AddParam(sectionGrid, "OptionLotsPerStep",
                "Multiplicator: во сколько раз ордер на покупку больше предыдущего. " +
                "1 - все входы одним объёмом (Начальный объём); " +
                "2 - объёмы удваиваются: 1, 2, 4, 8, 16 и т.д.",
                _optionLotsCombo);

            AddSectionHeader(content, "Дополнительно", out sectionGrid);
            _dynamicSelectionCombo = CreateEditableCombo("True (автоподбор)", "False (вручную)");
            AddParam(sectionGrid, "UseDynamicOptionSelection",
                "True - робот сам выбирает целевой опцион (страйк и экспирацию). " +
                "False - используется инструмент, заданный на вкладке опциона вручную (удобно для OsTester).",
                _dynamicSelectionCombo);

            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            // --- кнопки ---
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 14, 20, 14)
            };

            Button applyButton = CreateButton("Применить", _green, 150);
            applyButton.ToolTip = "Сохранить все параметры робота и закрыть окно.";
            applyButton.Click += (s, e) => ApplyAndClose();

            Button closeButton = CreateButton("Закрыть", _gray, 110);
            closeButton.ToolTip = "Закрыть окно. Изменения не сохраняются.";
            closeButton.Click += (s, e) => Close();

            buttons.Children.Add(applyButton);
            buttons.Children.Add(closeButton);

            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>
        /// Заголовок раздела на всю ширину + новая сетка под параметры в две колонки.
        /// </summary>
        private void AddSectionHeader(StackPanel content, string title, out Grid grid)
        {
            TextBlock header = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = _header,
                Margin = new Thickness(0, 12, 0, 4)
            };

            content.Children.Add(header);

            grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            content.Children.Add(grid);

            _sectionRows = 0;
        }

        /// <summary>
        /// Добавляет строку параметра в сетку (слева направо по колонкам).
        /// </summary>
        private void AddParam(Grid grid, string name, string hint, FrameworkElement control, Button extraButton = null)
        {
            int col = _sectionRows % 2;
            int row = _sectionRows / 2;

            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid cell = new Grid
            {
                Margin = new Thickness(0, 0, col == 0 ? 14 : 0, 10)
            };

            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // строка имени + значок вопроса
            StackPanel nameRow = new StackPanel { Orientation = Orientation.Horizontal };

            TextBlock nameText = new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = _textMain,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };

            TextBlock questionMark = new TextBlock
            {
                Text = "?",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = _accent,
                Width = 16,
                Height = 16,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Help,
                Margin = new Thickness(0, 0, 0, 0),
                IsHitTestVisible = false
            };

            // скругление значка вопроса
            Border questionBorder = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(9),
                Background = _accent,
                Cursor = Cursors.Help,
                Margin = new Thickness(0, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            questionBorder.Child = questionMark;
            ToolTipService.SetToolTip(questionBorder, CreateToolTip(name, hint));

            nameRow.Children.Add(nameText);
            nameRow.Children.Add(questionBorder);

            Grid.SetRow(nameRow, 0);
            cell.Children.Add(nameRow);

            // строка с контролом
            StackPanel controlRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 0)
            };

            control.Width = 210;
            control.HorizontalAlignment = HorizontalAlignment.Left;
            controlRow.Children.Add(control);

            if (extraButton != null)
            {
                controlRow.Children.Add(extraButton);
            }

            Grid.SetRow(controlRow, 1);
            cell.Children.Add(controlRow);

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);

            _sectionRows++;
        }

        /// <summary>
        /// Подсказка по параметру: описание + текущее значение и характеристики
        /// (диапазон/шаг для чисел, варианты для строковых списков).
        /// </summary>
        private ToolTip CreateToolTip(string name, string hint)
        {
            StackPanel panel = new StackPanel
            {
                MaxWidth = 380
            };

            TextBlock description = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brushes.Black
            };

            panel.Children.Add(description);

            if (_parameters.TryGetValue(name, out IIStrategyParameter parameter) && parameter != null)
            {
                string current = null;
                string characteristics = null;

                StrategyParameterInt intParameter = parameter as StrategyParameterInt;
                StrategyParameterDecimal decimalParameter = parameter as StrategyParameterDecimal;
                StrategyParameterString stringParameter = parameter as StrategyParameterString;
                StrategyParameterBool boolParameter = parameter as StrategyParameterBool;

                if (intParameter != null)
                {
                    current = intParameter.ValueInt.ToString();
                    characteristics = "Диапазон: " + intParameter.ValueIntStart + " .. " + intParameter.ValueIntStop +
                        (intParameter.ValueIntStep > 0 ? " (шаг " + intParameter.ValueIntStep + ")" : "");
                }
                else if (decimalParameter != null)
                {
                    current = decimalParameter.ValueDecimal.ToString(CultureInfo.InvariantCulture);
                    characteristics = "Диапазон: " +
                        decimalParameter.ValueDecimalStart.ToString(CultureInfo.InvariantCulture) + " .. " +
                        decimalParameter.ValueDecimalStop.ToString(CultureInfo.InvariantCulture) +
                        (decimalParameter.ValueDecimalStep > 0
                            ? " (шаг " + decimalParameter.ValueDecimalStep.ToString(CultureInfo.InvariantCulture) + ")"
                            : "");
                }
                else if (stringParameter != null)
                {
                    current = stringParameter.ValueString;
                    if (stringParameter.ValuesString != null && stringParameter.ValuesString.Count > 0)
                    {
                        characteristics = "Варианты: " + string.Join(", ", stringParameter.ValuesString);
                    }
                }
                else if (boolParameter != null)
                {
                    current = boolParameter.ValueBool ? "True" : "False";
                }

                TextBlock valueBlock = new TextBlock
                {
                    Text = "Текущее значение: " + current,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                panel.Children.Add(valueBlock);

                if (!string.IsNullOrEmpty(characteristics))
                {
                    TextBlock characteristicsBlock = new TextBlock
                    {
                        Text = characteristics,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = Brushes.Black,
                        Margin = new Thickness(0, 2, 0, 0)
                    };

                    panel.Children.Add(characteristicsBlock);
                }
            }

            return new ToolTip { Content = panel };
        }

        /// <summary>
        /// Тумблер: дорожка + бегунок + надпись ON/OFF.
        /// </summary>
        private FrameworkElement CreateRegimeToggle()
        {
            Grid g = new Grid
            {
                Width = 78,
                Height = 26,
                Cursor = Cursors.Hand
            };

            _regimePill = new Border
            {
                CornerRadius = new CornerRadius(13),
                Background = _gray,
                BorderBrush = _textDim,
                BorderThickness = new Thickness(1)
            };

            _regimeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _regimeKnob = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };

            g.Children.Add(_regimePill);
            g.Children.Add(_regimeText);
            g.Children.Add(_regimeKnob);

            g.MouseLeftButtonUp += (s, e) => ToggleRegime();

            return g;
        }

        private void UpdateRegimePill()
        {
            bool isOn = GetString("Regime") == "On";

            _regimePill.Background = isOn ? _green : _gray;
            _regimeText.Text = isOn ? "ON" : "OFF";
            _regimeKnob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _regimeKnob.Margin = isOn ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
            _regimeText.HorizontalAlignment = isOn ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _regimeText.Margin = isOn ? new Thickness(9, 0, 0, 0) : new Thickness(0, 0, 9, 0);
        }

        // ===================== Элементы =====================

        private ComboBox CreateEditableCombo(params string[] presets)
        {
            ComboBox combo = new ComboBox
            {
                IsEditable = true,
                Height = 28,
                FontSize = 13,
                Foreground = Brushes.White,
                Background = _bgControl,
                BorderBrush = _textDim,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0)
            };

            // тёмный выпадающий список: белый текст, синяя подсветка выбранного/наведённого пункта
            const string itemTemplateXaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
                " TargetType='{x:Type ComboBoxItem}'>" +
                "<Border x:Name='Bd' Background='#282B32' Padding='6,3'>" +
                "<ContentPresenter/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsHighlighted' Value='True'>" +
                "<Setter TargetName='Bd' Property='Background' Value='#2D6BFF'/>" +
                "</Trigger>" +
                "<Trigger Property='IsSelected' Value='True'>" +
                "<Setter TargetName='Bd' Property='Background' Value='#2D6BFF'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>";

            Style itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(itemTemplateXaml)));
            combo.ItemContainerStyle = itemStyle;

            // в редактируемом ComboBox текст в поле рисует внутренний TextBox,
            // который по умолчанию чёрный - принудительно делаем его белым на тёмном фоне
            combo.Loaded += (s, e) =>
            {
                TextBox box = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
                if (box != null)
                {
                    box.Foreground = Brushes.White;
                    box.Background = _bgControl;
                    box.CaretBrush = Brushes.White;
                    box.SelectionBrush = new SolidColorBrush(Color.FromRgb(45, 107, 255));
                }
            };

            for (int i = 0; i < presets.Length; i++)
            {
                combo.Items.Add(presets[i]);
            }

            return combo;
        }

        private Button CreateSmallButton(string text, RoutedEventHandler handler)
        {
            Button button = CreateButton(text, _accent, 90);
            button.Height = 26;
            button.Margin = new Thickness(6, 0, 0, 0);
            button.Click += handler;
            return button;
        }

        private Button CreateButton(string text, Brush color, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = color,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
        }

        // ===================== Логика =====================

        private void ToggleRegime()
        {
            string current = GetString("Regime");
            SetString("Regime", current == "On" ? "Off" : "On");
            UpdateRegimePill();
        }

        private void LoadValues()
        {
            UpdateRegimePill();

            _futuresSecurityCombo.Text = GetString("FuturesSecurityName");
            _optionBaseCombo.Text = GetString("OptionBaseAsset");
            _centralStrikeCombo.Text = GetString("CentralStrikeOverride");
            _stepCombo.Text = GetString("StrikeStep");
            _minDaysCombo.Text = GetString("OptionMinDaysToExpiry");
            _futuresVolumeCombo.Text = GetDecimal("FuturesVolumePerStep").ToString(CultureInfo.InvariantCulture);
            _optionLotsCombo.Text = GetDecimal("OptionLotsPerStep").ToString(CultureInfo.InvariantCulture);
            _maxStepsCombo.Text = GetInt("MaxSteps").ToString();
            _dynamicSelectionCombo.Text = GetBool("UseDynamicOptionSelection") ? "True (автоподбор)" : "False (вручную)";
        }

        private void RefreshFuturesList(object sender, RoutedEventArgs e)
        {
            try
            {
                List<string> names = _robot.RefreshFuturesSecurityList();

                _futuresSecurityCombo.Items.Clear();
                for (int i = 0; i < names.Count; i++)
                {
                    _futuresSecurityCombo.Items.Add(names[i]);
                }

                _futuresSecurityCombo.Text = ((StrategyParameterString)_robot.GetParametersForUi()["FuturesSecurityName"]).ValueString;
            }
            catch (Exception error)
            {
                MessageBox.Show(error.ToString(), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyAndClose()
        {
            try
            {
                // Режим уже записан при переключении тумблера
                SetString("FuturesSecurityName", _futuresSecurityCombo.Text);
                SetString("OptionBaseAsset", _optionBaseCombo.Text);
                SetString("CentralStrikeOverride", _centralStrikeCombo.Text);
                SetString("StrikeStep", _stepCombo.Text);
                SetString("OptionMinDaysToExpiry", _minDaysCombo.Text);
                SetDecimal("FuturesVolumePerStep", _futuresVolumeCombo.Text);
                SetDecimal("OptionLotsPerStep", _optionLotsCombo.Text);
                SetInt("MaxSteps", _maxStepsCombo.Text);
                SetBool("UseDynamicOptionSelection", _dynamicSelectionCombo.Text);

                _robot.OnParametersAppliedByUi();

                Close();
            }
            catch (Exception error)
            {
                MessageBox.Show("Не удалось применить параметры: " + error.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===================== Доступ к параметрам =====================

        private IIStrategyParameter GetParam(string name)
        {
            return _parameters.TryGetValue(name, out IIStrategyParameter value) ? value : null;
        }

        private string GetString(string name)
        {
            StrategyParameterString param = (StrategyParameterString)GetParam(name);
            return param != null ? param.ValueString : "";
        }

        private void SetString(string name, string value)
        {
            StrategyParameterString param = (StrategyParameterString)GetParam(name);
            if (param != null)
            {
                param.ValueString = value;
            }
        }

        private decimal GetDecimal(string name)
        {
            StrategyParameterDecimal param = (StrategyParameterDecimal)GetParam(name);
            return param != null ? param.ValueDecimal : 0;
        }

        private void SetDecimal(string name, string text)
        {
            StrategyParameterDecimal param = (StrategyParameterDecimal)GetParam(name);
            if (param == null)
            {
                return;
            }

            string clean = text.Trim();

            // "0 (авто)" -> 0
            int spaceIdx = clean.IndexOf(' ');
            if (spaceIdx > 0)
            {
                clean = clean.Substring(0, spaceIdx);
            }

            if (decimal.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) == false
                && decimal.TryParse(clean, NumberStyles.Float, CultureInfo.CurrentCulture, out value) == false)
            {
                throw new Exception("Параметр " + name + ": '" + text + "' - не число.");
            }

            param.ValueDecimal = value;
        }

        private int GetInt(string name)
        {
            StrategyParameterInt param = (StrategyParameterInt)GetParam(name);
            return param != null ? param.ValueInt : 0;
        }

        private void SetInt(string name, string text)
        {
            StrategyParameterInt param = (StrategyParameterInt)GetParam(name);
            if (param == null)
            {
                return;
            }

            if (int.TryParse(text.Trim(), out int value) == false)
            {
                throw new Exception("Параметр " + name + ": '" + text + "' - не целое число.");
            }

            param.ValueInt = value;
        }

        private bool GetBool(string name)
        {
            StrategyParameterBool param = (StrategyParameterBool)GetParam(name);
            return param != null && param.ValueBool;
        }

        private void SetBool(string name, string text)
        {
            StrategyParameterBool param = (StrategyParameterBool)GetParam(name);
            if (param == null)
            {
                return;
            }

            param.ValueBool = text.StartsWith("True", StringComparison.OrdinalIgnoreCase);
        }
    }
}
