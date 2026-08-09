using System.Windows;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Holds every user-facing string in each language and pushes the chosen set into the
/// application resources.
/// </summary>
/// <remarks>
/// The strings go in under <c>L_*</c> keys, and the XAML reads them with DynamicResource, so
/// swapping the language re-points every label at once with no per-control wiring. Code-behind
/// that builds text itself reads through <see cref="T"/> and rebuilds on <see cref="Changed"/>.
/// </remarks>
public static class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        // ---- main window chrome ----
        ["L_CloseTabTip"] = "Close tab (Ctrl+Shift+W)",
        ["L_Edit"] = "Edit",
        ["L_Delete"] = "Delete",
        ["L_CollapseSidebarTip"] = "Collapse the sidebar (Ctrl+Shift+B)",
        ["L_NewTabTip"] = "New tab (Ctrl+Shift+T)",
        ["L_ChooseShell"] = "Choose a shell",
        ["L_TabCard"] = "Tab card",
        ["L_CardColor"] = "Colour",
        ["L_CardOpacity"] = "Opacity",
        ["L_CardBlur"] = "Blur",
        ["L_CardImage"] = "Picture",
        ["L_CardIcon"] = "Icon (Segoe glyph)",
        ["L_CardReset"] = "Reset",
        ["L_CardRightClickHint"] = "Right-click a tab to change its colour, opacity and blur.",
        ["L_SplitSameTip"] = "Split: another shell on this SSH connection (Ctrl+Alt+D)",
        ["L_SplitNewTip"] = "Split: a new session alongside (Ctrl+Shift+D)",
        ["L_CloseSplitTip"] = "Close this split (Ctrl+Alt+A)",
        ["L_Minimize"] = "Minimize",
        ["L_Maximize"] = "Maximize",
        ["L_Close"] = "Close",
        ["L_MinimizeToTray"] = "Hide to the tray",
        ["L_TrayShow"] = "Show RedBloom",
        ["L_TrayExit"] = "Exit",
        ["L_RestartAdmin"] = "Restart as administrator",
        ["L_AlreadyAdmin"] = "Running as administrator",
        ["L_SshSessions"] = "SSH SESSIONS",
        ["L_NewSshSession"] = "New SSH session",
        ["L_SaveThisSession"] = "Save this connection to the sidebar?",
        ["L_SaveToastSaved"] = "Saved “{0}”.",
        ["L_Dismiss"] = "Dismiss",
        ["L_FilterHint"] = "Filter…",
        ["L_NoSavedSessions"] = "No saved sessions",
        ["L_UsePlusToAdd"] = "Use + above to add one",
        ["L_NoOpenTerminals"] = "No open terminals",
        ["L_EmptyHint"] = "Ctrl+Shift+T for a new tab  ·  double-click a session to connect",
        ["L_Settings"] = "Settings",

        // ---- settings: header ----
        ["L_Appearance"] = "Appearance",
        ["L_AppearanceNote"] = "Changes apply immediately and are saved as you go.",
        ["L_ResetDefaults"] = "Reset to defaults",

        // ---- settings: language ----
        ["L_Language"] = "Language",
        ["L_LanguageNote"] = "Language of the interface. Applies at once.",

        // ---- settings: integration ----
        ["L_Integration"] = "Integration",
        ["L_IntegrationNote"] = "Add RedBloom to Explorer so you can open a shell in any folder. Becoming the Windows default terminal needs a deeper OS integration and is not offered here.",
        ["L_OpenHere"] = "Open RedBloom here",
        ["L_OpenHereToggle"] = "Show \"Open RedBloom here\" in the folder context menu",

        // ---- settings: terminal text ----
        ["L_TerminalText"] = "Terminal text",
        ["L_TerminalTextNote"] = "Only fixed-width fonts are listed — a proportional font would break column alignment.",
        ["L_Font"] = "Font",
        ["L_Size"] = "Size",
        ["L_LineHeight"] = "Line height",
        ["L_Cursor"] = "Cursor",
        ["L_CursorBar"] = "Bar",
        ["L_CursorBlock"] = "Block",
        ["L_CursorUnderline"] = "Underline",
        ["L_Blink"] = "Blink",
        ["L_Scrollback"] = "Scrollback",

        // ---- settings: background picture ----
        ["L_BackgroundPicture"] = "Background picture",
        ["L_BackgroundPictureNote"] = "One picture behind the whole window, or a separate one per panel.",
        ["L_BgNone"] = "None",
        ["L_BgWindow"] = "Whole window",
        ["L_BgRegions"] = "Per panel",
        ["L_BgLive"] = "Live wallpaper",
        ["L_LiveNote"] = "Shows the desktop wallpaper itself, animation included — Wallpaper Engine scenes work. Blur, overlay and opacity below still apply.",
        ["L_Layout"] = "Layout",
        ["L_FollowDesktop"] = "Follow the desktop",
        ["L_FitInsideWindow"] = "Fit inside the window",
        ["L_LayoutNote"] = "Following the desktop shows the slice the window covers, as if it were see-through. Fitting draws the whole wallpaper inside the window, so moving it changes nothing.",
        ["L_FrameRate"] = "Frame rate",
        ["L_FrameNote"] = "Each frame costs CPU. Capture pauses automatically when the window is minimised or in the background.",
        ["L_TrimNote"] = "Trim edges — with the whole-desktop fallback the icons share the wallpaper surface and trimming keeps them out of shot. The Wallpaper Engine capture is already icon-free.",
        ["L_Left"] = "Left",
        ["L_Right"] = "Right",
        ["L_Top"] = "Top",
        ["L_Bottom"] = "Bottom",

        // ---- settings: backdrop editor ----
        ["L_Browse"] = "Browse",
        ["L_Clear"] = "Clear",
        ["L_Fit"] = "Fit",
        ["L_FitFillCrop"] = "Fill the panel (crop)",
        ["L_FitInside"] = "Fit inside",
        ["L_FitStretch"] = "Stretch to fill",
        ["L_FitOriginal"] = "Original size",
        ["L_PictureBlur"] = "Picture blur",
        ["L_PictureOpacity"] = "Picture opacity",
        ["L_OverlayColour"] = "Overlay colour",
        ["L_OverlayOpacity"] = "Overlay opacity",
        ["L_OverlayBlur"] = "Overlay blur",
        ["L_BackdropWholeWindow"] = "Whole window",
        ["L_BackdropSidebar"] = "Sidebar",
        ["L_BackdropTerminal"] = "Terminal",

        // ---- settings: window ----
        ["L_Window"] = "Window",
        ["L_WindowNote"] = "How the window sits among the others.",
        ["L_AlwaysOnTop"] = "Keep the window above all others",

        // ---- settings: see-through ----
        ["L_SeeThrough"] = "See-through",
        ["L_SeeThroughNote"] = "The window slider fades everything including the desktop behind it. The panel sliders only thin each panel's own colour, letting the background picture show through.",
        ["L_WholeWindow"] = "Whole window",
        ["L_Sidebar"] = "Sidebar",
        ["L_TabBar"] = "Tab bar",
        ["L_Terminal"] = "Terminal",

        // ---- settings: colours ----
        ["L_TerminalColours"] = "Terminal colours",
        ["L_TerminalColoursNote"] = "Accepts #RGB, #RRGGBB, #AARRGGBB or a colour name.",
        ["L_AnsiPalette"] = "ANSI palette",
        ["L_AnsiNote"] = "The sixteen colours programs address by name — ls, git and friends.",
        ["L_Application"] = "Application",
        ["L_ApplicationNote"] = "The window chrome: tab strip, sidebar, dialogs.",
        ["L_InterfaceFont"] = "Interface font",
        ["L_Preview"] = "Preview",
        ["L_PreviewNote"] = "How the palette reads against the terminal background.",

        // ---- settings: colour labels (built in code) ----
        ["L_ColBackground"] = "Background",
        ["L_ColText"] = "Text",
        ["L_ColCursor"] = "Cursor",
        ["L_ColSelection"] = "Selection",
        ["L_AnsiBlack"] = "Black",
        ["L_AnsiBrightBlack"] = "Bright black",
        ["L_AnsiRed"] = "Red",
        ["L_AnsiBrightRed"] = "Bright red",
        ["L_AnsiGreen"] = "Green",
        ["L_AnsiBrightGreen"] = "Bright green",
        ["L_AnsiYellow"] = "Yellow",
        ["L_AnsiBrightYellow"] = "Bright yellow",
        ["L_AnsiBlue"] = "Blue",
        ["L_AnsiBrightBlue"] = "Bright blue",
        ["L_AnsiMagenta"] = "Magenta",
        ["L_AnsiBrightMagenta"] = "Bright magenta",
        ["L_AnsiCyan"] = "Cyan",
        ["L_AnsiBrightCyan"] = "Bright cyan",
        ["L_AnsiWhite"] = "White",
        ["L_AnsiBrightWhite"] = "Bright white",
        ["L_ColAccent"] = "Accent",
        ["L_ColAccentDim"] = "Accent (dim)",
        ["L_ColSurface"] = "Surface",
        ["L_ColSurfaceRaised"] = "Surface (raised)",
        ["L_ColChrome"] = "Chrome",
        ["L_ColChromeHover"] = "Chrome (hover)",
        ["L_ColDivider"] = "Divider",
        ["L_ColTextMuted"] = "Text (muted)",
        ["L_ColTextFaint"] = "Text (faint)",

        // ---- units ----
        ["L_Fps"] = "{0} fps",

        // ---- common buttons ----
        ["L_Cancel"] = "Cancel",
        ["L_Save"] = "Save",
        ["L_Connect"] = "Connect",
        ["L_Remove"] = "Remove",

        // ---- session dialog ----
        ["L_NewSshSessionHeader"] = "New SSH session",
        ["L_EditSshSessionHeader"] = "Edit SSH session",
        ["L_PasteSshTip"] = "Fill this in from an ssh command on the clipboard",
        ["L_FieldName"] = "NAME",
        ["L_FieldHost"] = "HOST",
        ["L_FieldPort"] = "PORT",
        ["L_FieldUsername"] = "USERNAME",
        ["L_FieldAuth"] = "AUTHENTICATION",
        ["L_AuthPassword"] = "Password",
        ["L_AuthPrivateKey"] = "Private key",
        ["L_FieldPassword"] = "PASSWORD",
        ["L_PasswordDpapiNote"] = "Stored encrypted with Windows DPAPI under your account. Leave empty to be asked on each connect.",
        ["L_FieldKeyFile"] = "PRIVATE KEY FILE",
        ["L_FieldPassphrase"] = "PASSPHRASE",
        ["L_FieldTunnels"] = "TUNNELS",
        ["L_AddTunnelTip"] = "Add a tunnel",
        ["L_TunnelLocalPortTip"] = "Local port to listen on",
        ["L_TunnelDestHostTip"] = "Destination host, as the far end sees it",
        ["L_TunnelDestPortTip"] = "Destination port",
        ["L_FieldConnection"] = "CONNECTION",
        ["L_AutoReconnect"] = "Reconnect automatically if the connection drops",
        ["L_AutoReconnectNote"] = "Retries with a growing delay, up to five times. A session that stays up for a minute resets the count.",
        ["L_ErrClipboardRead"] = "Could not read the clipboard.",
        ["L_ErrClipboardEmpty"] = "The clipboard is empty. Copy an ssh command first.",
        ["L_ErrNotSshCommand"] = "That does not look like an ssh command.",
        ["L_ErrHostRequired"] = "Host is required.",
        ["L_ErrUserRequired"] = "Username is required.",
        ["L_ErrPortRange"] = "Port must be between 1 and 65535.",
        ["L_ErrChooseKey"] = "Choose a private key file.",
        ["L_ErrKeyMissing"] = "That private key file does not exist.",
        ["L_ErrTunnelIncomplete"] = "A tunnel is incomplete.",
        ["L_ErrTunnelDuplicate"] = "Two tunnels both listen on {0}.",

        // ---- password prompt ----
        ["L_PasswordRequired"] = "Password required",

        // ---- host key prompt ----
        ["L_PresentedFingerprint"] = "PRESENTED FINGERPRINT",
        ["L_PreviouslyTrusted"] = "PREVIOUSLY TRUSTED FINGERPRINT",
        ["L_ConnectOnce"] = "Connect once",
        ["L_TrustRemember"] = "Trust and remember",
        ["L_ReplaceStoredKey"] = "Replace stored key",
        ["L_CancelConnection"] = "Cancel connection",
        ["L_HkUnknownServer"] = "Unknown server",
        ["L_HkNewKeyType"] = "New key type for a known host",
        ["L_HkChanged"] = "Host key has changed",
        ["L_HkAlgoBits"] = "{0} · {1} bit",
        ["L_HkFirstBody"] = "RedBloom has not connected to {0} before, so it cannot tell whether this is the right machine. Check the fingerprint against the server before trusting it.",
        ["L_HkFirstHint"] = "On the server, `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` prints the fingerprint it should be showing.",
        ["L_HkNewAlgoBody"] = "{0} is already trusted, but has never presented a key of this type before. That is normal after a server reconfiguration — and is also what an interception attempt would look like. Verify the fingerprint.",
        ["L_HkNewAlgoHint"] = "If you did not change anything on the server, do not trust this key.",
        ["L_HkChangedBody"] = "The key presented by {0} does not match the one RedBloom trusted previously. This happens when a server is rebuilt or its keys are rotated — but it is also exactly what a machine-in-the-middle attack looks like. Do not continue unless you know why the key changed.",
        ["L_HkChangedHint"] = "Anything you type — including passwords — would go to whoever holds this key.",
    };

    private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>
    {
        ["L_CloseTabTip"] = "Закрыть вкладку (Ctrl+Shift+W)",
        ["L_Edit"] = "Изменить",
        ["L_Delete"] = "Удалить",
        ["L_CollapseSidebarTip"] = "Свернуть боковую панель (Ctrl+Shift+B)",
        ["L_NewTabTip"] = "Новая вкладка (Ctrl+Shift+T)",
        ["L_ChooseShell"] = "Выбрать оболочку",
        ["L_TabCard"] = "Карточка вкладки",
        ["L_CardColor"] = "Цвет",
        ["L_CardOpacity"] = "Непрозрачность",
        ["L_CardBlur"] = "Блюр",
        ["L_CardImage"] = "Картинка",
        ["L_CardIcon"] = "Значок (глиф Segoe)",
        ["L_CardReset"] = "Сбросить",
        ["L_CardRightClickHint"] = "ПКМ по вкладке — цвет, прозрачность и блюр.",
        ["L_SplitSameTip"] = "Сплит: ещё один шелл на этом SSH-подключении (Ctrl+Alt+D)",
        ["L_SplitNewTip"] = "Сплит: новая сессия рядом (Ctrl+Shift+D)",
        ["L_CloseSplitTip"] = "Закрыть этот сплит (Ctrl+Alt+A)",
        ["L_Minimize"] = "Свернуть",
        ["L_Maximize"] = "Развернуть",
        ["L_Close"] = "Закрыть",
        ["L_MinimizeToTray"] = "Свернуть в трей",
        ["L_TrayShow"] = "Показать RedBloom",
        ["L_TrayExit"] = "Выход",
        ["L_RestartAdmin"] = "Перезапустить от администратора",
        ["L_AlreadyAdmin"] = "Запущено от администратора",
        ["L_SshSessions"] = "SSH-СЕССИИ",
        ["L_NewSshSession"] = "Новая SSH-сессия",
        ["L_SaveThisSession"] = "Сохранить это подключение в боковую панель?",
        ["L_SaveToastSaved"] = "Сохранено «{0}».",
        ["L_Dismiss"] = "Скрыть",
        ["L_FilterHint"] = "Фильтр…",
        ["L_NoSavedSessions"] = "Нет сохранённых сессий",
        ["L_UsePlusToAdd"] = "Нажмите + выше, чтобы добавить",
        ["L_NoOpenTerminals"] = "Нет открытых терминалов",
        ["L_EmptyHint"] = "Ctrl+Shift+T — новая вкладка  ·  двойной клик по сессии для подключения",
        ["L_Settings"] = "Параметры",

        ["L_Appearance"] = "Внешний вид",
        ["L_AppearanceNote"] = "Изменения применяются сразу и сохраняются на ходу.",
        ["L_ResetDefaults"] = "Сбросить по умолчанию",

        ["L_Language"] = "Язык",
        ["L_LanguageNote"] = "Язык интерфейса. Применяется мгновенно.",

        ["L_Integration"] = "Интеграция",
        ["L_IntegrationNote"] = "Добавить RedBloom в Проводник, чтобы открывать шелл в любой папке. Стать терминалом Windows по умолчанию требует более глубокой интеграции с ОС и здесь не предлагается.",
        ["L_OpenHere"] = "Открыть RedBloom здесь",
        ["L_OpenHereToggle"] = "Показывать «Открыть RedBloom здесь» в контекстном меню папок",

        ["L_TerminalText"] = "Текст терминала",
        ["L_TerminalTextNote"] = "Показаны только моноширинные шрифты — пропорциональный сломал бы выравнивание столбцов.",
        ["L_Font"] = "Шрифт",
        ["L_Size"] = "Размер",
        ["L_LineHeight"] = "Высота строки",
        ["L_Cursor"] = "Курсор",
        ["L_CursorBar"] = "Черта",
        ["L_CursorBlock"] = "Блок",
        ["L_CursorUnderline"] = "Подчёркивание",
        ["L_Blink"] = "Мигание",
        ["L_Scrollback"] = "Буфер прокрутки",

        ["L_BackgroundPicture"] = "Фоновое изображение",
        ["L_BackgroundPictureNote"] = "Одна картинка за всем окном или отдельная для каждой панели.",
        ["L_BgNone"] = "Нет",
        ["L_BgWindow"] = "Всё окно",
        ["L_BgRegions"] = "По панелям",
        ["L_BgLive"] = "Живые обои",
        ["L_LiveNote"] = "Показывает сами обои рабочего стола, вместе с анимацией — сцены Wallpaper Engine работают. Блюр, наложение и прозрачность ниже всё равно применяются.",
        ["L_Layout"] = "Отображение",
        ["L_FollowDesktop"] = "Следовать за рабочим столом",
        ["L_FitInsideWindow"] = "Вписать в окно",
        ["L_LayoutNote"] = "«Следовать за рабочим столом» показывает область под окном, будто оно прозрачное. «Вписать» рисует все обои внутри окна, поэтому перемещение ничего не меняет.",
        ["L_FrameRate"] = "Частота кадров",
        ["L_FrameNote"] = "Каждый кадр тратит процессор. Захват сам встаёт на паузу, когда окно свёрнуто или в фоне.",
        ["L_TrimNote"] = "Обрезка краёв — в запасном режиме захвата всего рабочего стола иконки лежат на тех же обоях, и обрезка убирает их из кадра. Захват из Wallpaper Engine уже без иконок.",
        ["L_Left"] = "Слева",
        ["L_Right"] = "Справа",
        ["L_Top"] = "Сверху",
        ["L_Bottom"] = "Снизу",

        ["L_Browse"] = "Обзор",
        ["L_Clear"] = "Очистить",
        ["L_Fit"] = "Вписывание",
        ["L_FitFillCrop"] = "Заполнить панель (обрезка)",
        ["L_FitInside"] = "Вписать целиком",
        ["L_FitStretch"] = "Растянуть на всю",
        ["L_FitOriginal"] = "Исходный размер",
        ["L_PictureBlur"] = "Блюр картинки",
        ["L_PictureOpacity"] = "Непрозрачность картинки",
        ["L_OverlayColour"] = "Цвет наложения",
        ["L_OverlayOpacity"] = "Непрозрачность наложения",
        ["L_OverlayBlur"] = "Блюр наложения",
        ["L_BackdropWholeWindow"] = "Всё окно",
        ["L_BackdropSidebar"] = "Боковая панель",
        ["L_BackdropTerminal"] = "Терминал",

        ["L_Window"] = "Окно",
        ["L_WindowNote"] = "Как окно ведёт себя среди других.",
        ["L_AlwaysOnTop"] = "Держать окно поверх всех остальных",

        ["L_SeeThrough"] = "Прозрачность",
        ["L_SeeThroughNote"] = "Ползунок окна гасит всё, включая рабочий стол за ним. Ползунки панелей лишь разбавляют собственный цвет панели, пропуская фоновую картинку.",
        ["L_WholeWindow"] = "Всё окно",
        ["L_Sidebar"] = "Боковая панель",
        ["L_TabBar"] = "Панель вкладок",
        ["L_Terminal"] = "Терминал",

        ["L_TerminalColours"] = "Цвета терминала",
        ["L_TerminalColoursNote"] = "Принимает #RGB, #RRGGBB, #AARRGGBB или имя цвета.",
        ["L_AnsiPalette"] = "Палитра ANSI",
        ["L_AnsiNote"] = "Шестнадцать цветов, к которым программы обращаются по имени — ls, git и прочие.",
        ["L_Application"] = "Приложение",
        ["L_ApplicationNote"] = "Оформление окна: вкладки, боковая панель, диалоги.",
        ["L_InterfaceFont"] = "Шрифт интерфейса",
        ["L_Preview"] = "Предпросмотр",
        ["L_PreviewNote"] = "Как палитра читается на фоне терминала.",

        ["L_ColBackground"] = "Фон",
        ["L_ColText"] = "Текст",
        ["L_ColCursor"] = "Курсор",
        ["L_ColSelection"] = "Выделение",
        ["L_AnsiBlack"] = "Чёрный",
        ["L_AnsiBrightBlack"] = "Ярко-чёрный",
        ["L_AnsiRed"] = "Красный",
        ["L_AnsiBrightRed"] = "Ярко-красный",
        ["L_AnsiGreen"] = "Зелёный",
        ["L_AnsiBrightGreen"] = "Ярко-зелёный",
        ["L_AnsiYellow"] = "Жёлтый",
        ["L_AnsiBrightYellow"] = "Ярко-жёлтый",
        ["L_AnsiBlue"] = "Синий",
        ["L_AnsiBrightBlue"] = "Ярко-синий",
        ["L_AnsiMagenta"] = "Пурпурный",
        ["L_AnsiBrightMagenta"] = "Ярко-пурпурный",
        ["L_AnsiCyan"] = "Бирюзовый",
        ["L_AnsiBrightCyan"] = "Ярко-бирюзовый",
        ["L_AnsiWhite"] = "Белый",
        ["L_AnsiBrightWhite"] = "Ярко-белый",
        ["L_ColAccent"] = "Акцент",
        ["L_ColAccentDim"] = "Акцент (тусклый)",
        ["L_ColSurface"] = "Поверхность",
        ["L_ColSurfaceRaised"] = "Поверхность (приподнятая)",
        ["L_ColChrome"] = "Оформление",
        ["L_ColChromeHover"] = "Оформление (наведение)",
        ["L_ColDivider"] = "Разделитель",
        ["L_ColTextMuted"] = "Текст (приглушённый)",
        ["L_ColTextFaint"] = "Текст (бледный)",

        ["L_Fps"] = "{0} к/с",

        ["L_Cancel"] = "Отмена",
        ["L_Save"] = "Сохранить",
        ["L_Connect"] = "Подключиться",
        ["L_Remove"] = "Убрать",

        ["L_NewSshSessionHeader"] = "Новая SSH-сессия",
        ["L_EditSshSessionHeader"] = "Изменить SSH-сессию",
        ["L_PasteSshTip"] = "Заполнить из команды ssh в буфере обмена",
        ["L_FieldName"] = "ИМЯ",
        ["L_FieldHost"] = "ХОСТ",
        ["L_FieldPort"] = "ПОРТ",
        ["L_FieldUsername"] = "ПОЛЬЗОВАТЕЛЬ",
        ["L_FieldAuth"] = "АУТЕНТИФИКАЦИЯ",
        ["L_AuthPassword"] = "Пароль",
        ["L_AuthPrivateKey"] = "Приватный ключ",
        ["L_FieldPassword"] = "ПАРОЛЬ",
        ["L_PasswordDpapiNote"] = "Хранится в зашифрованном виде через Windows DPAPI под вашей учётной записью. Оставьте пустым, чтобы спрашивать при каждом подключении.",
        ["L_FieldKeyFile"] = "ФАЙЛ ПРИВАТНОГО КЛЮЧА",
        ["L_FieldPassphrase"] = "КЛЮЧЕВАЯ ФРАЗА",
        ["L_FieldTunnels"] = "ТУННЕЛИ",
        ["L_AddTunnelTip"] = "Добавить туннель",
        ["L_TunnelLocalPortTip"] = "Локальный порт для прослушивания",
        ["L_TunnelDestHostTip"] = "Целевой хост, как его видит удалённая сторона",
        ["L_TunnelDestPortTip"] = "Целевой порт",
        ["L_FieldConnection"] = "ПОДКЛЮЧЕНИЕ",
        ["L_AutoReconnect"] = "Переподключаться автоматически при обрыве",
        ["L_AutoReconnectNote"] = "Повторяет с растущей задержкой, до пяти раз. Сессия, продержавшаяся минуту, сбрасывает счётчик.",
        ["L_ErrClipboardRead"] = "Не удалось прочитать буфер обмена.",
        ["L_ErrClipboardEmpty"] = "Буфер обмена пуст. Сначала скопируйте команду ssh.",
        ["L_ErrNotSshCommand"] = "Это не похоже на команду ssh.",
        ["L_ErrHostRequired"] = "Укажите хост.",
        ["L_ErrUserRequired"] = "Укажите пользователя.",
        ["L_ErrPortRange"] = "Порт должен быть от 1 до 65535.",
        ["L_ErrChooseKey"] = "Выберите файл приватного ключа.",
        ["L_ErrKeyMissing"] = "Такого файла ключа не существует.",
        ["L_ErrTunnelIncomplete"] = "Туннель заполнен не полностью.",
        ["L_ErrTunnelDuplicate"] = "Два туннеля слушают один адрес {0}.",

        ["L_PasswordRequired"] = "Требуется пароль",

        ["L_PresentedFingerprint"] = "ПРЕДЪЯВЛЕННЫЙ ОТПЕЧАТОК",
        ["L_PreviouslyTrusted"] = "РАНЕЕ ДОВЕРЕННЫЙ ОТПЕЧАТОК",
        ["L_ConnectOnce"] = "Подключиться один раз",
        ["L_TrustRemember"] = "Доверять и запомнить",
        ["L_ReplaceStoredKey"] = "Заменить сохранённый ключ",
        ["L_CancelConnection"] = "Отменить подключение",
        ["L_HkUnknownServer"] = "Неизвестный сервер",
        ["L_HkNewKeyType"] = "Новый тип ключа для известного хоста",
        ["L_HkChanged"] = "Ключ хоста изменился",
        ["L_HkAlgoBits"] = "{0} · {1} бит",
        ["L_HkFirstBody"] = "RedBloom ещё не подключался к {0}, поэтому не может определить, тот ли это компьютер. Сверьте отпечаток с сервером, прежде чем доверять.",
        ["L_HkFirstHint"] = "На сервере `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` покажет отпечаток, который должен отображаться.",
        ["L_HkNewAlgoBody"] = "{0} уже в доверенных, но ещё ни разу не предъявлял ключ такого типа. Это нормально после перенастройки сервера — но так же выглядит и попытка перехвата. Сверьте отпечаток.",
        ["L_HkNewAlgoHint"] = "Если вы ничего не меняли на сервере, не доверяйте этому ключу.",
        ["L_HkChangedBody"] = "Ключ, предъявленный {0}, не совпадает с тем, которому RedBloom доверял раньше. Так бывает при пересборке сервера или смене ключей — но именно так выглядит и атака «человек посередине». Не продолжайте, пока не знаете, почему ключ изменился.",
        ["L_HkChangedHint"] = "Всё, что вы наберёте — включая пароли — уйдёт тому, кто владеет этим ключом.",
    };

    private static AppLanguage? _current;

    /// <summary>Raised after the language changes, for code that builds its own text.</summary>
    public static event Action? Changed;

    private static IReadOnlyDictionary<string, string> Table(AppLanguage language) =>
        language == AppLanguage.Russian ? Russian : English;

    /// <summary>Looks up a string in the current language, falling back to the key itself.</summary>
    public static string T(string key)
    {
        var table = Table(_current ?? AppLanguage.English);
        return table.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>
    /// Pushes the chosen language's strings into the application resources. A no-op when the
    /// language has not changed, so it is cheap to call from the shared apply path.
    /// </summary>
    public static void Apply(AppLanguage language)
    {
        if (_current == language)
        {
            return;
        }

        _current = language;

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // English is the full set of keys; writing over it each time keeps every key present
        // even if a translation is missing one.
        foreach (var (key, _) in English)
        {
            resources[key] = T(key);
        }

        Changed?.Invoke();
    }
}
