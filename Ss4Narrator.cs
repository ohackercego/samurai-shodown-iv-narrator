// Ss4Narrator - external (read-only) screen-reader narrator for SAMURAI SHODOWN V SPECIAL.
// Route D: ReadProcessMemory into the game's MenuManager/MainMenu structures; announce the
// current menu + selected item via NVDA (controller client), UIA notification, or SAPI.
// No code injection. Target: SSVS.exe (x86, image base 0x00400000, no ASLR).
//
// RE facts (confirmed live):
//   MenuManager vtable        = 0x004b9874
//   MenuManager + 0x1c        = current menu object pointer
//   MainMenu vtable           = 0x004b7fb4
//   menu + 0x6c               = label pointer array (char* keys)
//   menu + 0x78               = item count
//   menu + 0x28               = selected node name ("lineN", N 1-based)
//   menu + 0x94               = selected index (0-based, fallback)
//   label array[idx]          = char* key; resolved through Data\strings.txt (col1 key -> col3 en)
//
// Menu family layout (confirmed by vtable dump + decompiling the shared refresh FUN_004513d0):
//   Every menu class shares one base layout and a uniform 27-slot vtable, so +0x28 / +0x6c are
//   valid on all of them:
//     MainMenu 0x004b7fb4, OptionsMenu 0x004b8264, PauseMenu 0x004b8334, PopupMenu 0x004b8514,
//     OverlayMenu 0x004b9a3c, StandardMenu 0x004b9bb4, DummyMenu 0x004b977c, base Menu 0x004b970c
//   FUN_004513d0 (refresh) READS +0x78/+0x6c and creates nodes "line1".."lineN" via
//   FUN_004485d0("lineN", key->display, 0). The count at +0x78 is written by each menu's item
//   builder, so MainMenu/OptionsMenu/StandardMenu (which run it) have a valid count, while
//   PopupMenu/OverlayMenu leave +0x78 at allocator junk -> this narrator derives the count from
//   the label array in that case (see DeriveCount).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Ss4Narrator
{
    // ---- game RE constants ----
    const uint MENU_MANAGER_VTABLE = 0x0053BEC4u;  // IV: MenuManager::vftable (RTTI 0x57B3A0 -> locator 0x5513D4)
    const long OFF_MGR_MENU       = 0x1c;
    const long OFF_MGR_OPENER     = 0x24;   // IV: FUN_00467ca0 keeps the PREVIOUS active menu
                                            // (the one that pushed the current popup) at mgr+0x24
                                            // (mgr[0x24] = mgr[0x1c] before mgr[0x1c] = new).
    const long OFF_MENU_LABELARR  = 0x74;
    const long OFF_MENU_COUNT     = 0x80;
    const long OFF_MENU_SELNAME   = 0x28;
    const long OFF_MENU_SELIDX    = 0x94;
    const uint RDATA_LO = 0x004e7000u;  // IV .rdata
    const uint RDATA_HI = 0x00716000u;  // IV .data fim

    // ---- IV vtables (RTTI-measured live; NOT the V Special 0x004bxxxx values) ----
    // Enumerated by walking vtable-4 -> RTTICompleteObjectLocator -> TypeDescriptor in the
    // running NeoGeo.exe (tool: tools/enum_vt.cs, output tools/rtti_vt.tsv). Every "vt == 0x4bxxxx"
    // test elsewhere in this file is a V Special value and is DEAD in this port.
    const uint IVVT_Menu                = 0x0053BB74u;
    const uint IVVT_MainMenu            = 0x0053BA84u;
    const uint IVVT_OptionsMenu         = 0x0053C384u;
    const uint IVVT_PauseMenu           = 0x0053C504u;
    const uint IVVT_StandardMenu        = 0x0053D19Cu;
    const uint IVVT_DummyMenu           = 0x0053BDCCu;
    const uint IVVT_PopupMenu           = 0x0053C72Cu;
    const uint IVVT_OverlayMenu         = 0x0053C3FCu;
    const uint IVVT_InGameOptionsMenu   = 0x0053B3BCu;
    const uint IVVT_PlayPopupMenu       = 0x0053C634u;
    const uint IVVT_PopupInfoMenu       = 0x0053C6ACu;
    const uint IVVT_GameSettingsMenu    = 0x0053AEDCu;
    const uint IVVT_DisplaySettingsMenu = 0x0053ACBCu;
    const uint IVVT_VolumeSettingsMenu  = 0x0053D490u;
    const uint IVVT_SystemSettingsMenu  = 0x0053D2BCu;
    const uint IVVT_SettingsMenu        = 0x0053CE70u;
    const uint IVVT_HostSettingsMenu    = 0x0053B2D8u;
    const uint IVVT_ControlSettingsMenu = 0x0053A7A0u;
    const uint IVVT_CommandListMenu     = 0x0053A5C4u;
    const uint IVVT_CreditsMenu         = 0x0053ABE0u;
    const uint IVVT_GalleryMenu         = 0x0053AD8Cu;
    const uint IVVT_HiddenCharactersMenu= 0x0053AF7Cu;
    const uint IVVT_LeaderboardsMenu    = 0x0053B474u;
    const uint IVVT_LobbyMenu           = 0x0053B720u;
    const uint IVVT_LocalMenu           = 0x0053B9C8u;
    const uint IVVT_MultiplayerMenu     = 0x0053C1B0u;
    const uint IVVT_OnlinePlayMenu      = 0x0053C23Cu;
    const uint IVVT_RankingsMenu        = 0x0053C7CCu;
    const uint IVVT_RoomMenu            = 0x0053C9D4u;
    const uint IVVT_TrophiesMenu        = 0x0053D34Cu;
    const uint IVVT_ChangeGame          = 0x0053A38Cu;
    const uint IVVT_SaveDataErrorMenu   = 0x0053CCF4u;
    const uint IVVT_SaveDataInterstitial= 0x0053CDB0u;
    const uint IVVT_LoadDataInterstitial= 0x0053B520u;

    // IV offsets of the item table for the Settings family (SSVS +0x6c/+0x70/+0x7c/+0x80
    // shifted by +8; measured live on GameSettingsMenu/DisplaySettingsMenu/VolumeSettingsMenu:
    // +0x74 = 20-byte option rows, +0x78 = char* key array, +0x84 = sel index, +0x88 = count).
    const long IV_OFF_SET_ITEMS = 0x74;
    const long IV_OFF_SET_KEYS  = 0x78;
    const long IV_OFF_SET_IDX   = 0x84;
    const long IV_OFF_SET_COUNT = 0x88;
    const long IV_OFF_SET_COUNT2= 0xa4;

    // ---- IV: ControlSettingsMenu (vtable 0x0053A7A0) ----
    // This class is NOT a SettingsMenu: FUN_0045c8d0 builds its row labels from the engine's own
    // STATIC key table (0x00577C50, 17 char* -> "control", "arrow_up/down/left/right", "start",
    // "option_control_1".."option_control_11") and writes the translated text into the
    // "left_line%d"/"right_line%d" nodes with FUN_0047eaf0. There is no char** inside the object.
    // Row state (decompiled from FUN_0045dbd0 / FUN_0045ec50):
    //   menu+0x124 = selected row of the LEFT (P1) pane  (int)
    //   menu+0x128 = selected row of the RIGHT (P2) pane (int)
    //   menu+0x17c / +0x180 = animated scroll of each pane (float)
    //   menu+0x174 / +0x175 = per-pane flags (byte)
    // menu+0xC = the menu ID. FUN_004651a0 creates this class for id 6 AND for id 0x2E, and the
    // id changes the SCREEN GEOMETRY (measured, see below).
    //
    // PROVA do id usado pelo IV (estatico): ao escolher a linha "key_configuration" (Controles) do
    // menu de Opcoes, FUN_00461660 @0x4616c6 faz `FUN_004850f0(3); FUN_00467e80(6);`
    // -> FUN_004651a0(6, mgr) -> case 6 -> FUN_0045c8d0(mgr, 6) -> menu+0xC = 6.
    //   (Ao lado: "video_options" -> 5, "sound_options" -> 7, "back_caps" -> FUN_00467460.)
    // Logo o IV usa id 6 = a variante de PC: 17 linhas MENOS a ultima (ver flag) e COM a linha
    // "Dispositivo" (id 0x2E seria a variante de gabinete: 16 linhas, sem "Dispositivo", valores
    // das linhas desenhados como "key_%d").
    const long IV_CTRL_KEYTABLE = 0x00577C50;
    const long IV_OFF_CTRL_MENUID = 0x0C;
    const long IV_OFF_CTRL_ROW_L = 0x124;
    const long IV_OFF_CTRL_ROW_R = 0x128;
    const long IV_OFF_CTRL_PANE  = 0x174;

    // ---- IV: DE ONDE VEM O VALOR DE CADA LINHA (medido/desmontado 28/10) ----
    // O texto digitado em cada linha NAO fica no no (vira textura). O valor sai dos DADOS do
    // mapeamento, lidos assim pelo motor (FUN_0045e670, chamado pelo ctor e por FUN_0045dbd0):
    //   tabela 0x005793D0, 6 entradas de 16 bytes (a 7a e terminador):
    //     [0] = tabela PADRAO (em .data, 17 registros de 8 bytes: {mascara, codigo})
    //     [1] = tabela VIVA  (array que o motor poe no socket do jogador)
    //     [2] = jogador (1/2)     [3] = dispositivo (0=teclado, 1=gamepad DirectInput, 0x168=XInput)
    //   valor da linha = dword em [1] + registro*8 + 4, com registro = linha - 1 (linha 0 =
    //   "Dispositivo", sem valor). MEDIDO ao vivo: P1 (0x6B0D08): Cima=200 (0xC8, DIK_UP),
    //   Baixo=208, Esquerda=203, Direita=205, Start=57, A=8, B=9, C=10, D=11, A+B=22, B+C=24,
    //   C+D=38, A+B+C=23, A+B+C+D=25, Continuar=37  (bate byte a byte com Data/~windows.bin).
    //   Quadro de renderizacao (FUN_0045e670): teclado(0) -> "key_%d" via FUN_00409ca0 (tab.
    //   strings.txt), XInput(0x168) -> "button_360_%d", gamepad generico(1) -> switch 0..3,
    //   0x10..0x13 = "button_360_%d", resto = "button_generic" ("Botao %d"); -1 = "--------".
    // TIPO DE DISPOSITIVO por jogador: 0x006B1078+4*jogador (escrito por FUN_0048fe60 =
    // FUN_00428180(handle)); handle em 0x00579440+4*jogador. MEDIDO ao vivo nesta maquina (sem
    // controle plugado): tipos = [0,0], handles = [1,2], socket ativo P1 = 0x6B0D08 (= tabela do
    // TECLADO) e nomes de dispositivo vazios -> jogador no teclado.
    // ATENCAO (divergencia medida entre a TELA e o mapeamento em uso): com tipo 0 o motor troca a
    // entrada consultada por 0x168 (FUN_0045e670 @0x45e8b5..0x45e8be) e desenha na tela os nomes
    // de BOTAO do pad XInput, enquanto o mapeamento realmente em uso e o do teclado (o motor
    // poe a tabela do teclado no socket: 0x00555BA8+4*jogador = 0x6B0D08/0x6B0D90, medido).
    // Este narrador fala o mapeamento EM USO (a tecla real) e registra a variante da tela no log.
    const long IV_CTRL_ENTRY_TBL  = 0x005793D0;
    const long IV_CTRL_DEV_TYPE   = 0x006B1078;
    const long IV_CTRL_HANDLE     = 0x00579440;
    const long IV_CTRL_SOCKET     = 0x00555BA8;
    const long IV_CTRL_FLAG_PTR   = 0x00714E34;
    const long IV_CTRL_DEVNAME    = 0x00581180;
    const int  IV_CTRL_DEVNAME_STRIDE = 0x270;

    // ---- IV: PopupMenu (vtable 0x0053C72C) ----
    // FUN_0046d510 stores the two choice keys at menu+0x80 (char**) and the popup mode at
    // menu+0x84. Mode 0 hides the array (the "choice*" nodes carry their own text) and mode 1/2
    // translate param_4[0]/param_4[1] into the two choice nodes, so the array is only trusted
    // when the mode is 1 or 2.
    const long IV_OFF_POPUP_LABELS = 0x80;
    const long IV_OFF_POPUP_MODE   = 0x84;

    // ---- IV: onde o motor guarda o VALOR ATUAL de cada opcao ----
    // FUN_004740c0 (carregador do Options/Options.csv) monta TRES copias de uma unica string de
    // pares de 2 bytes "<Setting ID><digito>", por exemplo
    //   "N0E5H1F0D3B0R0Q2X1L1G7I7J7Z1V0W0A1C0O1M0S1U1Y0K0P0"
    //   +0x631B24 = padroes de fabrica (coluna "Default" do Options.csv)
    //   +0x631B40 = padroes corrigidos com Data/~options.bin (opcoes salvas pelo jogador)
    //   +0x631B44 = copia viva da lista "Unsaved" (FUN_00483190 / FUN_00483fa0)
    // FUN_00482ab0(id) e o GETTER do proprio motor: varre 0x631B44, depois 0x631B40, depois
    // 0x631B24 e devolve pair[1]-'0'. FUN_00481c40(id,val) e o SETTER (grava pair[1]=val+'0').
    // As telas de configuracao usam exatamente esses dois: vtable+0x80 = FUN_004733d0 ->
    // FUN_00482ab0 (ler) e vtable+0x84 = FUN_00473180 -> FUN_00481c40 (gravar ao mudar a linha).
    // Ou seja: o que este narrador le e o que o motor MOSTRA na linha.
    // MEDIDO AO VIVO (28/10): lendo a cadeia para as 9 linhas da tela Configuracoes do Jogo e
    // comparando com a escolha destacada na tela, bate 9 de 9 (Tempo de jogo=90/N1,
    // Rodadas 1J vs AI=2:2/E5, Rodadas 2J=2/H1, Mostrar nivel=On/F0, Nivel=4/D3,
    // Como Jogar=Off/B1, Som de demonstracao=On/R0, Violencia=3/Q2, Mostrar movimentos=Off/X1);
    // e 0x631B40 ja divergia dos padroes do CSV/~options.bin (N1, B1, G4) = a cadeia e viva.
    const uint IV_OPT_LIVE    = 0x00631B44;
    const uint IV_OPT_USER    = 0x00631B40;
    const uint IV_OPT_DEFAULT = 0x00631B24;

    // ---- Win32 ----
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string lpPathName);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, int nSize);
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);
    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDesktop(int dwThreadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    const uint TOKEN_QUERY = 0x0008;
    const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    static extern int GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation
    );

    // ---- Prism universal speech engine ----
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr prism_init(IntPtr cfg);
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr prism_registry_acquire_best(IntPtr ctx);
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr prism_backend_name(IntPtr backend);
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int prism_backend_initialize(IntPtr backend);
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int prism_backend_speak(IntPtr backend, byte[] utf8Bytes, bool interrupt);
    [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void prism_shutdown(IntPtr ctx);

    // ---- NVDA controller client ----
    [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
    static extern int nvdaController_speakText(string text);
    [DllImport("nvdaControllerClient.dll")]
    static extern int nvdaController_testIfRunning();
    [DllImport("nvdaControllerClient.dll")]
    static extern int nvdaController_cancelSpeech();

    // ---- UIA notification ----
    [DllImport("UIAutomationCore.dll")]
    static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IntPtr provider);
    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    static extern int UiaRaiseNotificationEvent(IntPtr provider, int notificationKind, int notificationProcessing, string displayString, string activityId);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public int __pad1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public int __pad2;
    }

    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_READWRITE = 0x04;
    const uint PAGE_WRITECOPY = 0x08;
    const uint PAGE_EXECUTE_READWRITE = 0x40;
    const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    const int CHUNK = 4 * 1024 * 1024;

    // ---- runtime state ----
    static IntPtr hProc = IntPtr.Zero;
    static long targetModuleBase = 0x00400000L;
    static long baseDelta = 0;
    static long mgr = 0;
    static string currentLang = "pt";
    static int lastIvLangIdx = -1;
    static int candEst1 = -1, candEst1Count = 0;
    static bool styleArmedP1 = false;
    static bool styleArmedP2 = false;
    static bool nivelArmed = false;
    static bool nivelArmedP2 = false;
    static int candNivel2 = -1, candNivel2Count = 0;
    static int lastNivel2 = -1;
    static int candNivel = -1, candNivelCount = 0;
    static int lastNivel = -1;
    static int candEst2 = -1, candEst2Count = 0;   // ultimo indice de idioma lido do jogo (0=ja,1=en,2=es,3=pt)
    static Dictionary<string, string> stringMap = new Dictionary<string, string>();
    static Dictionary<string, Dictionary<string, string>> allStrings = new Dictionary<string, Dictionary<string, string>>();
    static string cachedStringsPath = null;
    static string lastDedup = null;
    // (o campo lastLangMenu foi removido: a lista de idiomas agora e falada na entrada E em
    // cada navegacao da linha, montada no caminho geral das telas de configuracao.)
    static bool verbose = false;
    static IntPtr prismCtx = IntPtr.Zero;
    static IntPtr prismBackend = IntPtr.Zero;
    static string prismBackendName = null;
    static bool mute = false;
    static bool shouldExit = false;
    static string speakerMode = "auto";
    static bool[] keyWasDown = new bool[256];
    static string manualGamePath = null;

    static object sapi = null;
    static IntPtr uiaProvider = IntPtr.Zero;
    static Form uiaForm = null;

    static uint dynamicMenuManagerVtable = 0;
    static long dynamicMenuManagerPtr = 0;
    static long dynamicNeoBankTable = 0;

    static int IndexOfBytes(byte[] source, byte[] pattern, int start)
    {
        for (int i = start; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    static void AutoScanTargetSignatures(IntPtr h, long modBase)
    {
        try
        {
            Log("--- Iniciando Auto-Scan dinamico de assinaturas no SSVS.exe ---");
            int scanSize = 1024 * 1024; // 1 MB
            byte[] text = ReadBytes(h, modBase, scanSize);
            if (text == null || text.Length < 0x1000)
            {
                Log("[Auto-Scan] Nao foi possivel ler os primeiros bytes do modulo.");
                return;
            }

            // 1. Scan for MenuManager constructor: 33 c0 89 46 04 89 46 10 89 46 14 d9 ee
            byte[] ctorPat = new byte[] { 0x33, 0xc0, 0x89, 0x46, 0x04, 0x89, 0x46, 0x10, 0x89, 0x46, 0x14, 0xd9, 0xee };
            int ctorPos = IndexOfBytes(text, ctorPat, 0);
            if (ctorPos != -1)
            {
                for (int i = ctorPos; i < ctorPos + 30 && i + 6 <= text.Length; i++)
                {
                    if (text[i] == 0xc7 && text[i + 1] == 0x06)
                    {
                        dynamicMenuManagerVtable = BitConverter.ToUInt32(text, i + 2);
                        Log(string.Format("[Auto-Scan] VTable MenuManager descoberta: 0x{0:X}", dynamicMenuManagerVtable));
                        break;
                    }
                }
            }

            // 2. Scan for MenuManager instantiation call and global pointer write:
            // 8b f0 eb 02 33 f6 53 56 -> within 35 bytes find 89 35 [ptr]
            byte[] ptrPat = new byte[] { 0x8b, 0xf0, 0xeb, 0x02, 0x33, 0xf6, 0x53, 0x56 };
            int ptrPos = IndexOfBytes(text, ptrPat, 0);
            if (ptrPos != -1)
            {
                for (int i = ptrPos; i < ptrPos + 40 && i + 6 <= text.Length; i++)
                {
                    if (text[i] == 0x89 && text[i + 1] == 0x35)
                    {
                        uint p = BitConverter.ToUInt32(text, i + 2);
                        dynamicMenuManagerPtr = p;
                        Log(string.Format("[Auto-Scan] Ponteiro global MenuManager descoberto: 0x{0:X}", dynamicMenuManagerPtr));
                        break;
                    }
                }
            }

            // 3. Scan for 68000 bank table: c1 e9 10 8b 0c 8d or c1 e8 10 8b 04 85 or c1 e9 10 8b 04 8d
            byte[][] bankPats = new byte[][]
            {
                new byte[] { 0xc1, 0xe9, 0x10, 0x8b, 0x0c, 0x8d },
                new byte[] { 0xc1, 0xe9, 0x10, 0x8b, 0x04, 0x8d },
                new byte[] { 0xc1, 0xe8, 0x10, 0x8b, 0x04, 0x85 }
            };
            foreach (byte[] bp in bankPats)
            {
                int bPos = IndexOfBytes(text, bp, 0);
                if (bPos != -1 && bPos + bp.Length + 4 <= text.Length)
                {
                    dynamicNeoBankTable = BitConverter.ToUInt32(text, bPos + bp.Length);
                    Log(string.Format("[Auto-Scan] Tabela Neo-Geo RAM descoberta: 0x{0:X}", dynamicNeoBankTable));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("[Auto-Scan] Erro durante auto-scan: " + ex.Message);
        }
    }

    static long ResolveAddr(long origVa)
    {
        return origVa + baseDelta;
    }

    static long GetTargetModuleBase(IntPtr hp)
    {
        try
        {
            IntPtr[] mods = new IntPtr[256];
            uint needed;
            // 0x03 = LIST_MODULES_ALL, 0x01 = LIST_MODULES_32BIT
            if (EnumProcessModulesEx(hp, mods, (uint)(IntPtr.Size * mods.Length), out needed, 0x03) && needed > 0)
            {
                long baseAddr = mods[0].ToInt64();
                if (baseAddr >= 0x10000) return baseAddr;
            }
        }
        catch (Exception ex)
        {
            Log("GetTargetModuleBase aviso: " + ex.Message);
        }
        return 0x00400000L;
    }

    static string logFilePath = null;
    static readonly object logLock = new object();

    static void InitLog()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logFilePath = Path.Combine(baseDir, "log.txt");
            string header = string.Format(
                "================================================================================\r\n" +
                "  Ss4Narrator Log Iniciado: {0}\r\n" +
                "  PID: {1} | 64-bit Process: {2} | OS: {3}\r\n" +
                "  Diretorio Base: {4}\r\n" +
                "================================================================================\r\n",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Process.GetCurrentProcess().Id,
                Environment.Is64BitProcess,
                Environment.OSVersion,
                baseDir
            );
            File.WriteAllText(logFilePath, header, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try
            {
                logFilePath = Path.Combine(Path.GetTempPath(), "ssvs_narrator_log.txt");
                File.WriteAllText(logFilePath, "Fallback log: " + ex.Message + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    static void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [pid=" + Process.GetCurrentProcess().Id + "] " + msg;
        try { Console.WriteLine(line); } catch { }

        lock (logLock)
        {
            try
            {
                if (logFilePath == null) InitLog();
                File.AppendAllText(logFilePath, line + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    static void KillPreviousInstances()
    {
        try
        {
            int currentPid = Process.GetCurrentProcess().Id;
            Process[] ps = Process.GetProcessesByName("Ss4Narrator");
            foreach (Process p in ps)
            {
                try
                {
                    if (p.Id != currentPid)
                    {
                        Log("[INSTANCIA ANTERIOR] Encontrada instancia anterior em execucao (PID=" + p.Id + "). Finalizando...");
                        p.Kill();
                        p.WaitForExit(2000);
                        Log("[INSTANCIA ANTERIOR] Instancia anterior PID=" + p.Id + " finalizada com sucesso.");
                    }
                }
                catch (Exception ex)
                {
                    Log("[INSTANCIA ANTERIOR] Nao foi possivel finalizar PID=" + p.Id + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Erro ao verificar instancias anteriores: " + ex.Message);
        }
    }

    static void EnableDebugPrivilege()
    {
        try
        {
            IntPtr hToken;
            if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
            {
                try
                {
                    LUID luid;
                    if (LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                    {
                        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                        tp.PrivilegeCount = 1;
                        tp.Luid = luid;
                        tp.Attributes = SE_PRIVILEGE_ENABLED;
                        AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                        Log("SeDebugPrivilege habilitado com sucesso.");
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Aviso ao habilitar SeDebugPrivilege: " + ex.Message);
        }
    }

    static bool RelaunchAsAdmin(string[] args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Assembly.GetExecutingAssembly().Location;
            psi.Arguments = string.Join(" ", args);
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log("RelaunchAsAdmin failed: " + ex.Message);
            return false;
        }
    }

    static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    uint exitCode;
                    if (GetExitCodeProcess(h, out exitCode))
                        return exitCode == 259; // STILL_ACTIVE = 259
                }
                finally
                {
                    CloseHandle(h);
                }
            }

            Process p = Process.GetProcessById(pid);
            try { return !p.HasExited; }
            catch { return true; } // If Access Denied, process is running!
        }
        catch { return false; }
    }

    static Mutex singleInstanceMutex = null;

    [STAThread]
    static int Main(string[] args)
    {
        InitLog();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => {
            Log("FATAL: " + e.ExceptionObject);
        };
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Log("Ss4Narrator iniciado. Argumentos: " + (args.Length == 0 ? "(nenhum)" : string.Join(" ", args)));

        KillPreviousInstances();

        bool createdNew;
        singleInstanceMutex = new Mutex(true, @"Local\SsvsNarrator_SingleInstance_Mahi", out createdNew);
        if (!createdNew)
        {
            Log("Aviso: Outra instancia ainda respondia ao Mutex. Continuando como nova sessao ativa...");
        }

        int pid = 0;
        string stringsPath = null;
        int interval = 120;
        int durationSec = 0;
        bool listOnly = false;
        bool dumpOnly = false;
        bool ctrlProbe = false;
        bool ctrlRecite = false;
        string menuAt = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-Pid" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) pid = v; }
            else if ((a == "-Game" || a == "-Path" || a == "-Exe") && i + 1 < args.Length) manualGamePath = args[++i];
            else if (a == "-Strings" && i + 1 < args.Length) stringsPath = args[++i];
            else if (a == "-Interval" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) interval = v; }
            else if (a == "-Speaker" && i + 1 < args.Length) speakerMode = args[++i].ToLowerInvariant();
            else if (a == "-Duration" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) durationSec = v; }
            else if (a == "-Verbose") verbose = true;
            else if (a == "-Mute") mute = true;
            else if (a == "--list") listOnly = true;
            else if (a == "--dump") dumpOnly = true;
            else if (a == "--menu-at" && i + 1 < args.Length) menuAt = args[++i];
            else if (a == "--ctrl-probe") ctrlProbe = true;
            else if (a == "--ctrl-recite") ctrlRecite = true;
        }

        if (string.IsNullOrEmpty(manualGamePath))
            manualGamePath = LoadSavedGamePath();

        Log("Caminho do jogo (config/argumento): " + (manualGamePath ?? "(nenhum configurado)"));

        try { SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }

        Log("--- Verificando sintetizadores de voz ---");
        int nvdaStatus = -1;
        try { nvdaStatus = nvdaController_testIfRunning(); } catch (Exception ex) { Log("nvdaController_testIfRunning falhou: " + ex.Message); }
        Log("NVDA rodando: " + (nvdaStatus == 0 ? "SIM (0)" : "NAO (" + nvdaStatus + ")"));

        bool prismOk = false;
        try { prismOk = InitPrism(); } catch (Exception ex) { Log("InitPrism falhou: " + ex.Message); }
        Log("Prism backend: " + (prismOk ? prismBackendName : "Nao disponivel"));

        Log("Testando emissao de fala inicial...");
        Speak("Narrador Samurai Shodown V Special ativado.");

        // resolve target pid first when we need to derive the strings path
        Log("Buscando processo SSVS...");
        if (pid == 0) pid = FindPid();
        if (pid != 0) Log("Processo SSVS localizado inicialmente: PID=" + pid);
        else Log("Processo SSVS nao encontrado imediatamente. Entrando no loop de espera...");

        if (stringsPath == null)
            stringsPath = DeriveStringsPath(pid);
        cachedStringsPath = stringsPath;
        Log("Arquivo de strings definido: " + (stringsPath ?? "(nenhum localizado ainda)"));
        PreloadAllStrings(stringsPath);
        SetLanguage("pt");
        Log("Chaves de texto carregadas: " + stringMap.Count);

        if (listOnly)
        {
            Console.WriteLine("strings file : " + (stringsPath == null ? "(none)" : stringsPath));
            Console.WriteLine("keys loaded  : " + stringMap.Count);
            return 0;
        }

        if (pid == 0)
        {
            if (dumpOnly || listOnly)
            {
                Console.WriteLine("SSVS process not found. Start the game, or pass -Pid N.");
                return 1;
            }
            Console.WriteLine("Aguardando o jogo Samurai Shodown V Special (SSVS.exe) iniciar...");
            Console.WriteLine("[DICA] Se o jogo ja estiver aberto ou em outro local, pressione ESPACO ou 'O' para selecionar o SSVS.exe pelo Explorer do Windows.");
            Log("Waiting for SSVS.exe to start...");
            int waitCount = 0;
            while (!shouldExit && pid == 0)
            {
                pid = FindPid();
                if (pid != 0) break;

                // Check if user pressed SPACE, ENTER, or 'O' in console to browse for game executable manually
                try
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo ki = Console.ReadKey(true);
                        if (ki.Key == ConsoleKey.Spacebar || ki.Key == ConsoleKey.O || ki.Key == ConsoleKey.Enter)
                        {
                            string chosen = PromptForGamePath();
                            if (!string.IsNullOrEmpty(chosen))
                            {
                                manualGamePath = chosen;
                                pid = FindPid();
                                if (pid != 0) break;
                            }
                        }
                    }
                }
                catch { }

                waitCount++;
                // If waiting for more than 15 seconds (30 * 500ms) and manual path is not set, offer dialog automatically
                if (waitCount == 30 && string.IsNullOrEmpty(manualGamePath))
                {
                    Console.WriteLine("\nNao foi possivel detectar o jogo automaticamente ainda.");
                    Console.WriteLine("Abrindo janela do Windows Explorer para voce selecionar o SSVS.exe...");
                    string chosen = PromptForGamePath();
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        manualGamePath = chosen;
                        pid = FindPid();
                        if (pid != 0) break;
                    }
                }

                Thread.Sleep(500);
            }
            if (shouldExit) return 0;
            if (stringsPath == null) stringsPath = DeriveStringsPath(pid);
            if (cachedStringsPath == null) cachedStringsPath = stringsPath;
            if (allStrings.Count == 0) PreloadAllStrings(stringsPath);
            SetLanguage(currentLang);
        }

        EnableDebugPrivilege();

        hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) // ERROR_ACCESS_DENIED
            {
                Log("OpenProcess returned ERROR_ACCESS_DENIED (5) for pid=" + pid + ". Requesting admin elevation via UAC...");
                Console.WriteLine("\n[ACESSO NEGADO] O jogo SSVS.exe esta rodando com permissoes elevadas.");
                Console.WriteLine("Solicitando permissao de Administrador (UAC) para conectar ao jogo...");
                if (RelaunchAsAdmin(args))
                {
                    return 0;
                }
            }
            Console.WriteLine("OpenProcess failed (err=" + err + "). Tente executar o narrador como Administrador.");
            return 1;
        }

        targetModuleBase = GetTargetModuleBase(hProc);
        baseDelta = targetModuleBase - 0x00400000L;
        Log(string.Format("SSVS.exe conectado! PID={0} Base=0x{1:X} Delta=0x{2:X} Strings={3}", pid, targetModuleBase, baseDelta, stringMap.Count));

        AutoScanTargetSignatures(hProc, targetModuleBase);

        Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            shouldExit = true;
        };

        // Notify user audibly that the game process was connected and how to enter menus
        Speak("Jogo conectado. Se estiver na tela de apresentação, aperte Enter, Espaço ou Start no controle.");

        mgr = GetManager(hProc);
        if (mgr != 0) Log("MenuManager ativo inicialmente em: 0x" + mgr.ToString("X"));
        else Log("MenuManager ainda nao inicializado pelo jogo (aguardando usuario abrir menus)...");

        if (dumpOnly)
        {
            if (mgr == 0) { Console.WriteLine("MenuManager not found."); CloseHandles(); return 2; }
            Poll(true);
            CloseHandles();
            return 0;
        }

        // --menu-at <hex addr>: run the real menu resolution against an ARBITRARY menu object and
        // print/log the result, then exit. Used to prove the label resolution per class on a menu
        // object that is still alive in the heap even when it is not the manager's current menu.
        if (ctrlRecite)
        {
            // Executa o MESMO caminho do atalho F7 (recita o mapeamento). Prova no log.txt.
            if (hProc == IntPtr.Zero) { Console.WriteLine("sem processo"); return 1; }
            ReciteControls();
            Thread.Sleep(400);
            CloseHandles();
            return 0;
        }

        if (ctrlProbe)
        {
            // Diagnostico: exercita o MESMO codigo do bloco 5 (valores da tela de Controles) sem
            // falar nada. Nao precisa da tela aberta: le a tabela de rotulos e os DADOS do
            // mapeamento (dispositivo/tipo/handle por jogador). Usado para conferir a logica.
            if (hProc == IntPtr.Zero) { Console.WriteLine("sem processo"); return 1; }
            if (mgr == 0) mgr = ScanForManager(hProc);
            uint pm = (mgr != 0) ? ReadU32(hProc, mgr + OFF_MGR_MENU) : 0;
            int pid6 = (pm >= 0x10000) ? CtrlMenuId(pm) : -1;   // id do menu ABERTO agora (so informativo)
            int menuId = 6;   // 6 = variante PC usada pelo IV (provado); 0x2E = variante teclado
            Console.WriteLine("[ctrl-probe] menu aberto=0x" + pm.ToString("X") + " id=" + pid6 +
                              " | simulando a tela de Controles com id=" + menuId);
            for (int pane = 0; pane < 2; pane++)
            {
                int hnd = (int)ReadU32(hProc, ResolveAddr(IV_CTRL_HANDLE) + pane * 4);
                if (pane == 1 && hnd == 0) break;
                Console.WriteLine("[ctrl-probe] === Jogador " + (pane + 1) + " tipo=" + CtrlDeviceType(pane) +
                                  " handle=" + hnd + " socket=0x" +
                                  ReadU32(hProc, ResolveAddr(IV_CTRL_SOCKET) + (long)(pane + 1) * 4).ToString("X") +
                                  " esperado=0x" + CtrlEntryArr(pane + 1, CtrlDeviceType(pane)).ToString("X"));
                int rt = CtrlRowTotal(menuId);
                for (int r = 0; r < rt; r++)
                {
                    string lab = CtrlRowLabelText(menuId, r);
                    string sv; int code;
                    string val = CtrlRowValue(menuId, pane, r, out sv, out code);
                    Console.WriteLine("[ctrl-probe] " + (r + 1) + "/" + rt + " " + lab + " -> " + val +
                                      "  (codigo=" + code + " tela='" + sv + "')");
                }
            }
            CloseHandles();
            return 0;
        }

        if (menuAt != null)
        {
            long addr;
            try { addr = Convert.ToInt64(menuAt, 16); }
            catch { Console.WriteLine("--menu-at precisa de um endereco hexadecimal"); CloseHandles(); return 1; }
            uint ivt = ReadU32(hProc, addr);
            Console.WriteLine("menu=0x" + addr.ToString("X8") + " vt=0x" + ivt.ToString("X8") + " name=" + MenuName(ivt));
            PollMenu(hProc, (uint)addr, ivt, true);
            Console.WriteLine("lastDedup=" + lastDedup);
            CloseHandles();
            return 0;
        }

        Poll(true); // initial announcement

        Stopwatch sw = Stopwatch.StartNew();
        long lastPoll = 0;
        long lastHeartbeat = 0;

        while (!shouldExit)
        {
            long now = sw.ElapsedMilliseconds;
            if (durationSec > 0 && now >= (long)durationSec * 1000) break;

            if (pid == 0 || !IsProcessAlive(pid))
            {
                int np = FindPid();
                if (np != 0 && np != pid)
                {
                    if (hProc != IntPtr.Zero) CloseHandle(hProc);
                    pid = np;
                    hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
                    targetModuleBase = GetTargetModuleBase(hProc);
                    baseDelta = targetModuleBase - 0x00400000L;
                    mgr = 0;
                    ramBase = 0;
                    lastDedup = null;

                    string dynStrings = DeriveStringsPath(pid);
                    if (dynStrings != null)
                    {
                        cachedStringsPath = dynStrings;
                        PreloadAllStrings(dynStrings);
                        SetLanguage(currentLang);
                    }
                    Log(string.Format("SSVS.exe reconectado! PID={0} Base=0x{1:X}", pid, targetModuleBase));
                }
            }

            if (Edge(0x76)) ReciteControls();              // F7  recita todo o mapeamento de Controles
            if (Edge(0x77)) Poll(true);                    // F8  repeat current
            if (Edge(0x78)) SpeakMenuName();               // F9  menu name only
            if (Edge(0x79))                                // F10 toggle mute
            {
                mute = !mute;
                Console.WriteLine(mute ? "MUTE on" : "MUTE off");
                if (!mute) Poll(true);
            }
            if (Edge(0x7A)) break;                         // F11 quit (primary: 0x7A = VK_F11)
            if (Edge(0x7B)) break;                         // F12 quit (alt; only seen with Ctrl/Alt held)

            if (now - lastPoll >= interval)
            {
                Poll(false);
                lastPoll = now;
            }

            if (now - lastHeartbeat >= 5000)
            {
                Log(string.Format("[Heartbeat] PID={0} Mgr=0x{1:X} NeoRAM=0x{2:X}", pid, mgr, ramBase));
                lastHeartbeat = now;
            }

            try { Application.DoEvents(); } catch { }
            Thread.Sleep(15);
        }

        Speak("Ssvs narrator stopped.");
        CloseHandles();
        return 0;
    }

    static void CloseHandles()
    {
        if (hProc != IntPtr.Zero) { CloseHandle(hProc); hProc = IntPtr.Zero; }
        if (uiaForm != null) { try { uiaForm.Dispose(); } catch { } uiaForm = null; }
        if (prismCtx != IntPtr.Zero)
        {
            try { prism_shutdown(prismCtx); } catch { }
            prismCtx = IntPtr.Zero;
            prismBackend = IntPtr.Zero;
        }
    }

    static string GetConfigFilePath()
    {
        try
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssvs_path.cfg");
        }
        catch
        {
            return "ssvs_path.cfg";
        }
    }

    static string LoadSavedGamePath()
    {
        try
        {
            string cfg = GetConfigFilePath();
            if (File.Exists(cfg))
            {
                string line = File.ReadAllText(cfg, Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    if (File.Exists(line)) return line;
                    string maybeExe = Path.Combine(line, "NeoGeo.exe");
                    if (File.Exists(maybeExe)) return maybeExe;
                }
            }
        }
        catch { }
        return null;
    }

    static void SaveGamePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            string cfg = GetConfigFilePath();
            File.WriteAllText(cfg, path.Trim(), Encoding.UTF8);
            Log("Game path saved to config: " + path);
        }
        catch { }
    }

    static string PromptForGamePath()
    {
        string selectedPath = null;
        Thread t = new Thread(() =>
        {
            try
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "Selecione o executavel do jogo (SSVS.exe)";
                    ofd.Filter = "Executavel do Jogo (SSVS.exe)|SSVS.exe|Todos os Executaveis (*.exe)|*.exe|Todos os Arquivos (*.*)|*.*";
                    ofd.CheckFileExists = true;
                    ofd.CheckPathExists = true;
                    ofd.RestoreDirectory = true;

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        selectedPath = ofd.FileName;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("PromptForGamePath dialog error: " + ex.Message);
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        if (!string.IsNullOrEmpty(selectedPath) && File.Exists(selectedPath))
        {
            Console.WriteLine("Caminho do jogo selecionado: " + selectedPath);
            SaveGamePath(selectedPath);
            return selectedPath;
        }
        return null;
    }

    static string GetProcessPath(int targetPid)
    {
        if (targetPid == 0) return null;
        try
        {
            Process p = Process.GetProcessById(targetPid);
            string fn = p.MainModule.FileName;
            if (!string.IsNullOrEmpty(fn) && File.Exists(fn)) return fn;
        }
        catch { }

        IntPtr hp = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, targetPid);
        if (hp != IntPtr.Zero)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hp, 0, sb, ref size) && size > 0)
                {
                    string path = sb.ToString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                sb.Length = 0;
                if (GetModuleFileNameEx(hp, IntPtr.Zero, sb, sb.Capacity) > 0)
                {
                    string path = sb.ToString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            catch { }
            finally
            {
                CloseHandle(hp);
            }
        }
        return null;
    }

    static int FindPid()
    {
        // 0. If user provided or saved a specific game path, match against running processes
        if (!string.IsNullOrEmpty(manualGamePath))
        {
            try
            {
                string targetDir = Path.GetDirectoryName(Path.GetFullPath(manualGamePath));
                Process[] all = Process.GetProcesses();
                for (int i = 0; i < all.Length; i++)
                {
                    Process p = all[i];
                    try
                    {
                        string fullPath = GetProcessPath(p.Id);
                        if (!string.IsNullOrEmpty(fullPath))
                        {
                            if (string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(manualGamePath), StringComparison.OrdinalIgnoreCase))
                                return p.Id;
                            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(fullPath)), targetDir, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(Path.GetFileName(fullPath), "NeoGeo.exe", StringComparison.OrdinalIgnoreCase))
                                return p.Id;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 1. Direct process name lookup (case-insensitive)
        try
        {
            Process[] ps = Process.GetProcessesByName("NeoGeo");
            if (ps != null && ps.Length > 0)
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        int targetId = ps[i].Id;
                        if (targetId > 0 && IsProcessAlive(targetId))
                            return targetId;
                    }
                    catch { }
                }
            }
        }
        catch { }

        try
        {
            Process[] ps = Process.GetProcessesByName("neogeo");
            if (ps != null && ps.Length > 0)
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        int targetId = ps[i].Id;
                        if (targetId > 0 && IsProcessAlive(targetId))
                            return targetId;
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. Scan all running processes for SSVS.exe (handles any renamed process or launcher scenario)
        try
        {
            Process[] all = Process.GetProcesses();
            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    string pName = p.ProcessName;
                    if (string.Equals(pName, "NeoGeo", StringComparison.OrdinalIgnoreCase))
                        return p.Id;

                    string fullPath = GetProcessPath(p.Id);
                    if (!string.IsNullOrEmpty(fullPath))
                    {
                        string exeName = Path.GetFileName(fullPath);
                        if (string.Equals(exeName, "NeoGeo.exe", StringComparison.OrdinalIgnoreCase))
                            return p.Id;
                    }
                }
                catch { }
            }
        }
        catch { }

        return 0;
    }

    static string DeriveStringsPath(int pid)
    {
        // 0. Manual path specified by user or loaded from config
        if (!string.IsNullOrEmpty(manualGamePath))
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(manualGamePath));
                if (!string.IsNullOrEmpty(dir))
                {
                    string p1 = Path.Combine(dir, "Data", "strings.txt");
                    if (File.Exists(p1)) return p1;
                    string p2 = Path.Combine(dir, "strings.txt");
                    if (File.Exists(p2)) return p2;
                }
            }
            catch { }
        }

        // 1. Dynamic check: Process directory from live PID
        if (pid != 0)
        {
            string exePath = GetProcessPath(pid);
            if (!string.IsNullOrEmpty(exePath))
            {
                string dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    string p1 = Path.Combine(Path.Combine(dir, "Data"), "strings.txt");
                    if (File.Exists(p1)) return p1;
                    string p2 = Path.Combine(dir, "strings.txt");
                    if (File.Exists(p2)) return p2;
                }
            }
        }

        // 2. Relative search from narrator's own location:
        //    (e.g., if Ss4Narrator is in <GameDir>\mod\ or <GameDir>\ or any subfolder)
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            string[] relativeCandidates = new string[]
            {
                Path.Combine(baseDir, "Data", "strings.txt"),
                Path.Combine(baseDir, "..", "Data", "strings.txt"),
                Path.Combine(baseDir, "..", "..", "Data", "strings.txt"),
                Path.Combine(baseDir, "strings.txt"),
                Path.Combine(baseDir, "..", "strings.txt")
            };
            for (int i = 0; i < relativeCandidates.Length; i++)
            {
                try
                {
                    string full = Path.GetFullPath(relativeCandidates[i]);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }

        // 3. Check all fixed / standard Steam library locations across all active drives
        DriveInfo[] drives = DriveInfo.GetDrives();
        for (int i = 0; i < drives.Length; i++)
        {
            try
            {
                DriveInfo d = drives[i];
                if (!d.IsReady) continue;
                string root = d.RootDirectory.FullName;
                string[] steamCandidates = new string[]
                {
                    Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt"),
                    Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt"),
                    Path.Combine(root, "SteamLibrary", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt"),
                    Path.Combine(root, "Steam", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt"),
                    Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt"),
                    Path.Combine(root, "Jogos", "SteamLibrary", "steamapps", "common", "SAMURAI SHODOWN V SPECIAL", "Data", "strings.txt")
                };
                for (int j = 0; j < steamCandidates.Length; j++)
                {
                    if (File.Exists(steamCandidates[j])) return steamCandidates[j];
                }
            }
            catch { }
        }

        return null;
    }

    static void PreloadAllStrings(string path)
    {
        allStrings.Clear();
        allStrings["pt"] = LoadStrings(path, "pt");
        allStrings["en"] = LoadStrings(path, "en");
        allStrings["es"] = LoadStrings(path, "es");
        allStrings["ja"] = LoadStrings(path, "ja");
    }

    static void SetLanguage(string lang)
    {
        if (lang == null) lang = "pt";
        lang = lang.ToLowerInvariant();
        if (lang != "pt" && lang != "en" && lang != "es" && lang != "ja") lang = "pt";
        currentLang = lang;
        if (allStrings.ContainsKey(lang))
            stringMap = allStrings[lang];
        else if (cachedStringsPath != null)
            stringMap = LoadStrings(cachedStringsPath, lang);
    }

    // Data\strings.txt: UTF-16LE, tab-separated, header on line 0:
    //   default_en  category  en  pt  es  ja
    // col0 = key, col2 = en, col3 = pt, col4 = es, col5 = ja.
    static Dictionary<string, string> LoadStrings(string path, string lang = "pt")
    {
        Dictionary<string, string> map = new Dictionary<string, string>();
        if (path == null || !File.Exists(path)) return map;

        string text;
        try { text = File.ReadAllText(path, Encoding.Unicode); }
        catch { return map; }

        string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int langCol = 3; // pt default
        if (lang == "en") langCol = 2;
        else if (lang == "es") langCol = 4;
        else if (lang == "ja") langCol = 5;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == 0) continue; // header
            string[] c = lines[i].Split('\t');
            if (c.Length < 3) continue;
            string key = c[0].Trim();
            if (key.Length == 0) continue;
            string disp = "";
            if (langCol < c.Length && c[langCol].Trim().Length > 0)
                disp = c[langCol].Trim();
            else if (c.Length > 2 && c[2].Trim().Length > 0)
                disp = c[2].Trim();
            else
                disp = key;
            if (!map.ContainsKey(key)) map[key] = disp;
        }
        return map;
    }

    static string FormatCommandText(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return "";
        string s = cmd.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (currentLang == "en")
        {
            s = s.Replace("→ ↘ ↓ ↙ ←→", "forward, half circle back, forward, ");
            s = s.Replace("→↘↓↙←→", "forward, half circle back, forward, ");
            s = s.Replace("↓ ↘ →", "down, down-forward, forward, ");
            s = s.Replace("↓↘→", "down, down-forward, forward, ");
            s = s.Replace("↓ ↙ ←", "down, down-back, back, ");
            s = s.Replace("↓↙←", "down, down-back, back, ");
            s = s.Replace("→↓ ↘", "forward, down, down-forward, ");
            s = s.Replace("→ ↓ ↘", "forward, down, down-forward, ");
            s = s.Replace("←↓ ↙", "back, down, down-back, ");
            s = s.Replace("← ↓ ↙", "back, down, down-back, ");

            s = s.Replace("↑", "up ");
            s = s.Replace("↓", "down ");
            s = s.Replace("←", "back ");
            s = s.Replace("→", "forward ");
            s = s.Replace("↘", "down-forward ");
            s = s.Replace("↙", "down-back ");
            s = s.Replace("↗", "up-forward ");
            s = s.Replace("↖", "up-back ");

            s = s.Replace("+ BCD", "plus Overkill");
            s = s.Replace("+ CD", "plus Rage");
            s = s.Replace("+ BC", "plus Throw");
            s = s.Replace("+ AB", "plus Heavy Slash");
            s = s.Replace("+ S", "plus Slash");
            s = s.Replace("+ A", "plus Light Slash");
            s = s.Replace("+ B", "plus Medium Slash");
            s = s.Replace("+ C", "plus Kick");
            s = s.Replace("+ D", "plus Special");
            s = s.Replace("+", "plus ");
        }
        else if (currentLang == "es")
        {
            s = s.Replace("→ ↘ ↓ ↙ ←→", "adelante, media luna atrás, adelante, ");
            s = s.Replace("→↘↓↙←→", "adelante, media luna atrás, adelante, ");
            s = s.Replace("↓ ↘ →", "abajo, diagonal adelante, adelante, ");
            s = s.Replace("↓↘→", "abajo, diagonal adelante, adelante, ");
            s = s.Replace("↓ ↙ ←", "abajo, diagonal atrás, atrás, ");
            s = s.Replace("↓↙←", "abajo, diagonal atrás, atrás, ");
            s = s.Replace("→↓ ↘", "adelante, abajo, diagonal adelante, ");
            s = s.Replace("→ ↓ ↘", "adelante, abajo, diagonal adelante, ");
            s = s.Replace("←↓ ↙", "atrás, abajo, diagonal atrás, ");
            s = s.Replace("← ↓ ↙", "atrás, abajo, diagonal atrás, ");

            s = s.Replace("↑", "arriba ");
            s = s.Replace("↓", "abajo ");
            s = s.Replace("←", "atrás ");
            s = s.Replace("→", "adelante ");
            s = s.Replace("↘", "diagonal abajo adelante ");
            s = s.Replace("↙", "diagonal abajo atrás ");
            s = s.Replace("↗", "diagonal arriba adelante ");
            s = s.Replace("↖", "diagonal arriba atrás ");

            s = s.Replace("+ BCD", "más Golpe Fatal");
            s = s.Replace("+ CD", "más Furia");
            s = s.Replace("+ BC", "más Agarre");
            s = s.Replace("+ AB", "más Corte Fuerte");
            s = s.Replace("+ S", "más Corte");
            s = s.Replace("+ A", "más Corte Débil");
            s = s.Replace("+ B", "mais Corte Medio");
            s = s.Replace("+ C", "más Patada");
            s = s.Replace("+ D", "más Especial");
            s = s.Replace("+", "más ");
        }
        else if (currentLang == "ja")
        {
            s = s.Replace("→ ↘ ↓ ↙ ←→", "前、半回転後ろ、前、");
            s = s.Replace("→↘↓↙←→", "前、半回転後ろ、前、");
            s = s.Replace("↓ ↘ →", "下、斜め前、前、");
            s = s.Replace("↓↘→", "下、斜め前、前、");
            s = s.Replace("↓ ↙ ←", "下、斜め後ろ、後ろ、");
            s = s.Replace("↓↙←", "下、斜め後ろ、後ろ、");
            s = s.Replace("→↓ ↘", "前、下、斜め前、");
            s = s.Replace("→ ↓ ↘", "前、下、斜め前、");
            s = s.Replace("←↓ ↙", "後ろ、下、斜め後ろ、");
            s = s.Replace("← ↓ ↙", "後ろ、下、斜め後ろ、");

            s = s.Replace("↑", "上 ");
            s = s.Replace("↓", "下 ");
            s = s.Replace("←", "後ろ ");
            s = s.Replace("→", "前 ");
            s = s.Replace("↘", "斜め下前 ");
            s = s.Replace("↙", "斜め下後ろ ");
            s = s.Replace("↗", "斜め上前 ");
            s = s.Replace("↖", "斜め上後ろ ");

            s = s.Replace("+ BCD", "+ 絶命奥義");
            s = s.Replace("+ CD", "+ 怒り爆発");
            s = s.Replace("+ BC", "+ 投げ");
            s = s.Replace("+ AB", "+ 強斬り");
            s = s.Replace("+ S", "+ 斬り");
            s = s.Replace("+ A", "+ 弱斬り");
            s = s.Replace("+ B", "+ 中斬り");
            s = s.Replace("+ C", "+ 蹴り");
            s = s.Replace("+ D", "+ 特殊");
        }
        else // default Portuguese
        {
            s = s.Replace("→ ↘ ↓ ↙ ←→", "frente, meia lua trás, frente, ");
            s = s.Replace("→↘↓↙←→", "frente, meia lua trás, frente, ");
            s = s.Replace("↓ ↘ →", "baixo, diagonal frente, frente, ");
            s = s.Replace("↓↘→", "baixo, diagonal frente, frente, ");
            s = s.Replace("↓ ↙ ←", "baixo, diagonal trás, trás, ");
            s = s.Replace("↓↙←", "baixo, diagonal trás, trás, ");
            s = s.Replace("→↓ ↘", "frente, baixo, diagonal frente, ");
            s = s.Replace("→ ↓ ↘", "frente, baixo, diagonal frente, ");
            s = s.Replace("←↓ ↙", "trás, baixo, diagonal trás, ");
            s = s.Replace("← ↓ ↙", "trás, baixo, diagonal trás, ");

            s = s.Replace("↑", "cima ");
            s = s.Replace("↓", "baixo ");
            s = s.Replace("←", "trás ");
            s = s.Replace("→", "frente ");
            s = s.Replace("↘", "diagonal baixo frente ");
            s = s.Replace("↙", "diagonal baixo trás ");
            s = s.Replace("↗", "diagonal cima frente ");
            s = s.Replace("↖", "diagonal cima trás ");

            s = s.Replace("+ BCD", "mais Golpe Fatal");
            s = s.Replace("+ CD", "mais Fúria");
            s = s.Replace("+ BC", "mais Arremesso");
            s = s.Replace("+ AB", "mais Corte Forte");
            s = s.Replace("+ S", "mais Golpe de Espada");
            s = s.Replace("+ A", "mais Corte Fraco");
            s = s.Replace("+ B", "mais Corte Médio");
            s = s.Replace("+ C", "mais Chute");
            s = s.Replace("+ D", "mais Especial");
            s = s.Replace("+", "mais ");
        }

        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Replace(" ,", ",");
        return s.Trim();
    }

    // ---- process memory helpers ----

    static byte[] ReadBytes(IntPtr h, long addr, int len)
    {
        byte[] buf = new byte[len];
        IntPtr read;
        if (!ReadProcessMemory(h, new IntPtr(addr), buf, new IntPtr(len), out read)) return null;
        if (read.ToInt64() < len) return null;
        return buf;
    }

    static uint ReadU32(IntPtr h, long addr)
    {
        byte[] b = ReadBytes(h, addr, 4);
        if (b == null) return 0;
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    static ushort ReadU16(IntPtr h, long addr)
    {
        byte[] b = ReadBytes(h, addr, 2);
        if (b == null) return 0;
        return (ushort)(b[0] | (b[1] << 8));
    }

    static byte ReadU8(IntPtr h, long addr)
    {
        byte[] b = ReadBytes(h, addr, 1);
        if (b == null) return 0;
        return b[0];
    }

    static string ReadCString(IntPtr h, long addr, int max)
    {
        if (addr <= 0x1000) return "";
        List<byte> raw = new List<byte>(max > 128 ? 128 : max);
        byte[] chunk = new byte[64];
        int read = 0;
        while (read < max)
        {
            int want = Math.Min(64, max - read);
            IntPtr got;
            if (!ReadProcessMemory(h, new IntPtr(addr + read), chunk, new IntPtr(want), out got) || got.ToInt64() <= 0)
            {
                byte[] one = ReadBytes(h, addr + read, 1);
                if (one == null || one[0] == 0) break;
                raw.Add(one[0]);
                read += 1;
                continue;
            }
            int n = (int)got.ToInt64();
            bool term = false;
            for (int i = 0; i < n; i++)
            {
                if (chunk[i] == 0) { term = true; break; }
                raw.Add(chunk[i]);
            }
            if (term) break;
            read += n;
        }
        try { return Encoding.UTF8.GetString(raw.ToArray()); }
        catch { return Encoding.Default.GetString(raw.ToArray()); }
    }

    // ---- manager discovery ----

    static uint lastScanTick = 0;

    static long GetManager(IntPtr h)
    {
        if (mgr != 0 && ValidateManager(h, mgr)) return mgr;

        // 0. Priority: Dynamically detected pointer from Auto-Scan
        if (dynamicMenuManagerPtr != 0)
        {
            uint candDyn = ReadU32(h, dynamicMenuManagerPtr);
            if (candDyn >= 0x10000 && candDyn <= 0x7fff0000 && ValidateManager(h, candDyn))
            {
                mgr = candDyn;
                return mgr;
            }
        }

        // Fast static check 1: Master MenuManager instance pointer stored at 0x005F0688
        long p1 = ResolveAddr(0x005FCE94); // IV: DAT_005FCE94 (ctor FUN_004659c0: DAT_005fce94 = param_1)
        uint cand1 = ReadU32(h, p1);
        if (cand1 >= 0x10000 && cand1 <= 0x7fff0000 && ValidateManager(h, cand1))
        {
            mgr = cand1;
            return mgr;
        }

        // Fast static check 2: Master UI Controller / Menu Interface stored at 0x005E85BC
        long p2 = ResolveAddr(0x005FCE94); // IV: unico global confirmado
        uint cand2 = ReadU32(h, p2);
        if (cand2 >= 0x10000 && cand2 <= 0x7fff0000 && ValidateManager(h, cand2))
        {
            mgr = cand2;
            return mgr;
        }

        // Fast static check 3: Alternative state pointer at 0x005F03D4
        long p3 = ResolveAddr(0x005FCE94); // IV: unico global confirmado
        uint cand3 = ReadU32(h, p3);
        if (cand3 >= 0x10000 && cand3 <= 0x7fff0000 && ValidateManager(h, cand3))
        {
            mgr = cand3;
            return mgr;
        }

        // Throttle fallback memory scan to at most once every 1000ms
        uint now = (uint)Environment.TickCount;
        if (now - lastScanTick < 1000) return 0;
        lastScanTick = now;

        mgr = ScanForManager(h);
        return mgr;
    }

    static long ScanForManager(IntPtr h)
    {
        uint targetVt = dynamicMenuManagerVtable != 0 ? dynamicMenuManagerVtable : (uint)ResolveAddr(MENU_MANAGER_VTABLE);
        byte[] pat = new byte[4];
        pat[0] = (byte)(targetVt & 0xff);
        pat[1] = (byte)((targetVt >> 8) & 0xff);
        pat[2] = (byte)((targetVt >> 16) & 0xff);
        pat[3] = (byte)((targetVt >> 24) & 0xff);

        MEMORY_BASIC_INFORMATION mbi;
        IntPtr mbiSize = new IntPtr(Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));
        long addr = 0x10000;
        const long MAX = 0x7fff0000L;

        while (addr < MAX)
        {
            if (VirtualQueryEx(h, new IntPtr(addr), out mbi, mbiSize) == IntPtr.Zero) break;
            long baseAddr = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) break;

            bool writable = mbi.State == MEM_COMMIT &&
                (mbi.Protect == PAGE_READWRITE || mbi.Protect == PAGE_WRITECOPY ||
                 mbi.Protect == PAGE_EXECUTE_READWRITE || mbi.Protect == PAGE_EXECUTE_WRITECOPY);

            if (writable)
            {
                long found = ScanRegion(h, baseAddr, regionSize, pat);
                if (found != 0) return found;
            }
            addr = baseAddr + regionSize;
        }
        return 0;
    }

    static long ScanRegion(IntPtr h, long baseAddr, long size, byte[] pat)
    {
        long off = 0;
        while (off < size)
        {
            long remain = size - off;
            int want = (int)Math.Min((long)CHUNK, remain);
            int readLen = want;
            if (off + want < size) readLen = want + 3; // overlap for cross-boundary match

            byte[] buf = new byte[readLen];
            IntPtr got;
            if (!ReadProcessMemory(h, new IntPtr(baseAddr + off), buf, new IntPtr(readLen), out got) || got.ToInt64() < 4)
            {
                off += want;
                continue;
            }
            int n = (int)got.ToInt64();
            for (int i = 0; i + 4 <= n; i += 4)
            {
                if (buf[i] == pat[0] && buf[i + 1] == pat[1] && buf[i + 2] == pat[2] && buf[i + 3] == pat[3])
                {
                    long cand = baseAddr + off + i;
                    if (ValidateManager(h, cand)) return cand;
                }
            }
            off += want;
        }
        return 0;
    }

    static bool ValidateManager(IntPtr h, long cand)
    {
        if (cand <= 0x1000 || cand > 0x7fff0000) return false;
        uint vtMgr = ReadU32(h, cand);
        uint expectedVt = dynamicMenuManagerVtable != 0 ? dynamicMenuManagerVtable : (uint)ResolveAddr(MENU_MANAGER_VTABLE);
        if (vtMgr == expectedVt) return true;

        long rLo = ResolveAddr(RDATA_LO);
        long rHi = ResolveAddr(RDATA_HI);
        if (vtMgr >= rLo && vtMgr <= rHi)
        {
            uint menu = ReadU32(h, cand + OFF_MGR_MENU);
            if (menu == 0) return true; // valid manager even when no menu is open yet!
            if (menu >= 0x10000 && menu <= 0x7fff0000)
            {
                uint vt = ReadU32(h, menu);
                if (vt >= rLo && vt <= rHi) return true;
            }
        }
        return false;
    }

    // Node names built by the engine are line%d / value%d / bar%d (FUN_0045ec50) plus
    // left_line%d / right_line%d (0x0053A9F0). All of those are 1-BASED. Returns the 0-based
    // index, or -1 if the name is not a node name.
    // NOTE: the PopupMenu family ("choice%d") is the exception - it is 0-BASED and is handled by
    // ChoiceNodeIndex() instead; "choice" is deliberately NOT accepted here.
    static int ParseNodeIndex(string s)
    {
        if (s == null || s.Length == 0) return -1;
        int i = s.Length;
        while (i > 0 && s[i - 1] >= '0' && s[i - 1] <= '9') i--;
        if (i == s.Length) return -1;
        string pre = s.Substring(0, i);
        if (!(pre.EndsWith("line") || pre.EndsWith("value") || pre.EndsWith("bar"))) return -1;
        int n;
        if (!int.TryParse(s.Substring(i), out n)) return -1;
        return n - 1;
    }

    // PopupMenu choice nodes are 0-BASED: the ctor writes the FIRST label through the literal
    // 0x0053C02C = "choice0" and FUN_0046da30 builds "choice%d" with i starting at 0, so the node
    // names are choice0/choice1/... (not choice1/choice2). This is the one node family that must
    // not go through ParseNodeIndex. Returns the raw 0-based index, or -1.
    static int ChoiceNodeIndex(string s)
    {
        if (s == null || !s.StartsWith("choice")) return -1;
        int n;
        if (!int.TryParse(s.Substring(6), out n)) return -1;
        return n;
    }

    static int ReadSelectedIndex(IntPtr h, long menu)
    {
        uint namePtr = ReadU32(h, menu + OFF_MENU_SELNAME);
        string s = ReadCString(h, namePtr, 32);
        int byName = ParseNodeIndex(s);
        if (byName >= 0) return byName;
        uint raw = ReadU32(h, menu + OFF_MENU_SELIDX);
        if (raw < 1000) return (int)raw;
        return -1;
    }

    // ---- label array resolution, per menu class ----
    // The IV classes do NOT share one offset for the item-key array: the plain menus keep it at
    // +0x74, while the whole Settings family keeps it at +0x78 and uses +0x74 for its 20-byte
    // option-row array (measured live - see watch_menu.txt). Rather than trusting a fixed
    // offset we look for the offset whose char** actually holds item keys that exist in
    // Data\strings.txt. That is self-validating, so it is correct per class (and per screen)
    // without a hardcoded table, and it degrades to "no label" instead of printing junk.
    static readonly long[] LABEL_OFF_CANDIDATES = new long[]
    { 0x74, 0x78, 0x70, 0x6c, 0x7c, 0x80, 0x84, 0x88, 0x8c, 0x90, 0x94, 0x98, 0x9c, 0xa0 };

    static readonly long[] SELIDX_OFF_CANDIDATES = new long[] { 0x94, 0x84, 0x8c, 0x7c, 0x80 };

    static bool ResolveLabelArray(IntPtr h, long menu, out long off, out uint arr, out int count)
    {
        off = 0; arr = 0; count = 0;
        int best = -1;
        for (int c = 0; c < LABEL_OFF_CANDIDATES.Length; c++)
        {
            long o = LABEL_OFF_CANDIDATES[c];
            uint a = ReadU32(h, menu + o);
            if (a < 0x00010000 || a > 0x7fff0000) continue;
            int n = 0, known = 0;
            while (n < 256)
            {
                uint kp = ReadU32(h, (long)a + (long)n * 4);
                if (kp < 0x00010000 || kp > 0x7fff0000) break;
                string ks = ReadCString(h, kp, 64);
                if (!IsPrintableKey(ks)) break;
                if (stringMap.ContainsKey(ks)) known++;
                n++;
            }
            if (n < 1) continue;
            // The located table can be longer than the menu's real item count (measured live:
            // MainMenu's key table at +0x74 holds 9 keys - play, options, main_quit, back, ... -
            // while the menu has 3 items and +0x80 says 3). Trust the class's count field when it
            // is inside the table, otherwise fall back to the table length (the Settings family
            // leaves +0x80 at 0 and its table length IS the row count).
            uint cntField = ReadU32(h, menu + OFF_MENU_COUNT);
            int nEff = n;
            if (cntField >= 1 && cntField <= (uint)n) nEff = (int)cntField;
            // an array whose entries are known label keys wins; longer arrays break ties
            int score = known * 1000 + n;
            if (known == 0) score = n;              // usable but not confirmed by strings.txt
            if (score > best) { best = score; off = o; arr = a; count = nEff; }
        }
        return best > 0 && count >= 1;
    }

    static string LabelKeyAt(IntPtr h, uint arr, int count, int idx)
    {
        if (idx < 0 || idx >= count) return "";
        uint kp = ReadU32(h, (long)arr + (long)idx * 4);
        if (kp < 0x00010000) return "";
        return ReadCString(h, kp, 64);
    }

    // A label key must be a non-empty run of printable ASCII. Menu keys are plain identifiers
    // (e.g. "local_game"), so this cheaply rejects RTTI blobs / binary tables that happen to
    // follow a label array in .rdata.
    static bool IsPrintableKey(string s)
    {
        if (s == null || s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 32 || c > 126) return false;
        }
        return true;
    }

    // Derive an item count by walking the inherited label array (menu + 0x6c) until an entry
    // stops looking like a key pointer. Module keys live in .rdata and point at printable text,
    // so the first non-conforming slot marks the end of the menu's items. Returns -1 if the
    // array doesn't look usable at all.
    static int DeriveCount(IntPtr h, uint arr)
    {
        int n = 0;
        while (n < 256)
        {
            uint kp = ReadU32(h, (long)arr + (long)n * 4);
            if (kp < RDATA_LO || kp > RDATA_HI) break;
            if (!IsPrintableKey(ReadCString(h, kp, 64))) break;
            n++;
        }
        return n;
    }

    static string L4(string en, string es, string ja, string pt)
    {
        if (currentLang == "en") return en;
        if (currentLang == "es") return es;
        if (currentLang == "ja") return ja;
        return pt;
    }

    // ---- menu name map ----
    static string MenuName(uint vt)
    {
        uint origVt = (uint)(vt - baseDelta);

        // SAMURAI SHODOWN IV: its own vtable set (RTTI-measured). Every one of these used to
        // fall through to "Menu" because the table below still held the V Special values.
        switch (origVt)
        {
            case IVVT_MainMenu:            return L4("Main Menu", "Menú Principal", "メインメニュー", "Menu Principal");
            case IVVT_MultiplayerMenu:     return L4("Multiplayer", "Multijugador", "マルチプレイヤー", "Multijogador");
            case IVVT_OnlinePlayMenu:      return L4("Online Match", "Partida Online", "オンライン対戦", "Partida Online");
            case IVVT_LobbyMenu:           return L4("Lobby", "Lobby", "ロビー", "Lobby");
            case IVVT_RoomMenu:            return L4("Room", "Sala", "ルーム", "Sala");
            case IVVT_GalleryMenu:         return L4("Gallery", "Galería", "ギャラリー", "Galeria");
            case IVVT_LeaderboardsMenu:    return L4("Leaderboards", "Marcadores", "リーダーボード", "Placares");
            case IVVT_TrophiesMenu:        return L4("Trophies and Achievements", "Trofeos y Logros", "トロフィー", "Troféus e Conquistas");
            case IVVT_OptionsMenu:         return L4("Options", "Opciones", "オプション", "Opções");
            case IVVT_SettingsMenu:        return L4("Settings", "Ajustes", "設定", "Configurações");
            case IVVT_SystemSettingsMenu:  return L4("System Settings", "Ajustes del Sistema", "システム設定", "Configurações do Sistema");
            case IVVT_GameSettingsMenu:    return L4("Game Settings", "Ajustes de Juego", "ゲーム設定", "Configurações do Jogo");
            case IVVT_DisplaySettingsMenu: return L4("Display Settings", "Ajustes de Pantalla", "画面設定", "Configurações de Tela");
            case IVVT_VolumeSettingsMenu:  return L4("Volume Settings", "Ajustes de Volumen", "音量設定", "Configurações de Volume");
            case IVVT_ControlSettingsMenu: return L4("Controls", "Controles", "操作設定", "Configurações de Controle");
            case IVVT_HostSettingsMenu:    return L4("Room Settings", "Ajustes de Sala", "ルーム設定", "Configurações da Sala");
            case IVVT_InGameOptionsMenu:   return L4("Match Options", "Opciones de Partida", "試合オプション", "Opções da Partida");
            case IVVT_HiddenCharactersMenu:return L4("Hidden Characters", "Personajes Secretos", "隠しキャラクター", "Personagens Secretos");
            case IVVT_CommandListMenu:     return L4("Command List", "Lista de Movimientos", "技表", "Lista de Golpes");
            case IVVT_CreditsMenu:         return L4("Credits", "Créditos", "クレジット", "Créditos");
            case IVVT_PauseMenu:           return L4("Pause Menu", "Menú de Pausa", "ポーズメニュー", "Menu de Pausa");
            case IVVT_PopupMenu:           return L4("Confirmation", "Confirmación", "確認", "Confirmação");
            case IVVT_PlayPopupMenu:       return L4("Match Menu", "Menú de Partida", "対戦メニュー", "Menu da Partida");
            case IVVT_PopupInfoMenu:       return L4("Information", "Información", "情報", "Informação");
            case IVVT_RankingsMenu:        return L4("Ranking", "Clasificación", "ランキング", "Classificação");
            case IVVT_LocalMenu:           return L4("Local Game", "Juego Local", "ローカル対戦", "Jogo Local");
            case IVVT_StandardMenu:        return L4("Menu", "Menú", "メニュー", "Menu");
            case IVVT_ChangeGame:          return L4("Switch Game", "Cambiar Juego", "ゲーム切替", "Trocar Jogo");
            case IVVT_SaveDataErrorMenu:   return L4("Save Data Error", "Error de Guardado", "セーブエラー", "Erro de Gravação");
            case IVVT_SaveDataInterstitial:return L4("Saving", "Guardando", "セーブ中", "Gravando");
            case IVVT_LoadDataInterstitial:return L4("Loading", "Cargando", "ロード中", "Carregando");
            case IVVT_OverlayMenu:         return L4("Overlay", "Superposición", "オーバーレイ", "Sobreposição");
        }

        switch (origVt)
        {
            case 0x4b7fb4: return (currentLang == "en") ? "Main Menu" : (currentLang == "es") ? "Menú Principal" : (currentLang == "ja") ? "メインメニュー" : "Menu Principal";
            case 0x4b807c: return (currentLang == "en") ? "Multiplayer" : (currentLang == "es") ? "Multijugador" : (currentLang == "ja") ? "マルチプレイヤー" : "Multijogador";
            case 0x4b8194: return (currentLang == "en") ? "Online Match" : (currentLang == "es") ? "Partida Online" : (currentLang == "ja") ? "オンライン対戦" : "Partida Online";
            case 0x4b7c54: return (currentLang == "en") ? "Lobby" : (currentLang == "es") ? "Lobby" : (currentLang == "ja") ? "ロビー" : "Lobby";
            case 0x4b8a8c: return (currentLang == "en") ? "Room" : (currentLang == "es") ? "Sala" : (currentLang == "ja") ? "ルーム" : "Sala";
            case 0x4b73a4: return (currentLang == "en") ? "Gallery" : (currentLang == "es") ? "Galería" : (currentLang == "ja") ? "ギャラリー" : "Galeria";
            case 0x4b7474: return (currentLang == "en") ? "Art Viewer" : (currentLang == "es") ? "Visor de Arte" : (currentLang == "ja") ? "アートビューアー" : "Visualizador de Arte";
            case 0x4b79cc: return (currentLang == "en") ? "Leaderboards" : (currentLang == "es") ? "Marcadores" : (currentLang == "ja") ? "リーダーボード" : "Placares de Líderes";
            case 0x4b8e24: return (currentLang == "en") ? "Trophies and Achievements" : (currentLang == "es") ? "Trofeos y Logros" : (currentLang == "ja") ? "トロフィー" : "Troféus e Conquistas";
            case 0x4b8264: return (currentLang == "en") ? "Options" : (currentLang == "es") ? "Opciones" : (currentLang == "ja") ? "オプション" : "Opções";
            case 0x4b8c64: return (currentLang == "en") ? "Settings" : (currentLang == "es") ? "Ajustes" : (currentLang == "ja") ? "設定" : "Configurações";
            case 0x4b8d8c: return (currentLang == "en") ? "System Settings" : (currentLang == "es") ? "Ajustes del Sistema" : (currentLang == "ja") ? "システム設定" : "Configurações do Sistema";
            case 0x4b7614: return (currentLang == "en") ? "Game Settings" : (currentLang == "es") ? "Ajustes de Juego" : (currentLang == "ja") ? "ゲーム設定" : "Configurações do Jogo";
            case 0x4b729c: return (currentLang == "en") ? "Display Settings" : (currentLang == "es") ? "Ajustes de Pantalla" : (currentLang == "ja") ? "画面設定" : "Configurações de Tela";
            case 0x4b919c: return (currentLang == "en") ? "Volume Settings" : (currentLang == "es") ? "Ajustes de Volumen" : (currentLang == "ja") ? "音量設定" : "Configurações de Volume";
            case 0x4b6dc4: return (currentLang == "en") ? "Controls" : (currentLang == "es") ? "Controles" : (currentLang == "ja") ? "操作設定" : "Configurações de Controle";
            case 0x4b786c: return (currentLang == "en") ? "Room Settings" : (currentLang == "es") ? "Ajustes de Sala" : (currentLang == "ja") ? "ルーム設定" : "Configurações da Sala";
            case 0x4b76b4: return (currentLang == "en") ? "Hidden Characters" : (currentLang == "es") ? "Personajes Secretos" : (currentLang == "ja") ? "隠しキャラクター" : "Personagens Secretos";
            case 0x4b6ac4: return (currentLang == "en") ? "Command List" : (currentLang == "es") ? "Lista de Movimientos" : (currentLang == "ja") ? "技表" : "Lista de Golpes";
            case 0x4b7094: return (currentLang == "en") ? "Credits" : (currentLang == "es") ? "Créditos" : (currentLang == "ja") ? "クレジット" : "Créditos";
            case 0x4b8334: return (currentLang == "en") ? "Pause Menu" : (currentLang == "es") ? "Menú de Pausa" : (currentLang == "ja") ? "ポーズメニュー" : "Menu de Pausa";
            case 0x4b7914: return (currentLang == "en") ? "Match Options" : (currentLang == "es") ? "Opciones de Partida" : (currentLang == "ja") ? "試合オプション" : "Opções da Partida";
            case 0x4b8424: return (currentLang == "en") ? "Match" : (currentLang == "es") ? "Partida" : (currentLang == "ja") ? "対戦" : "Partida";
            case 0x4b84a4: return (currentLang == "en") ? "Information" : (currentLang == "es") ? "Información" : (currentLang == "ja") ? "情報" : "Informação";
            case 0x4b8514: return (currentLang == "en") ? "Confirmation" : (currentLang == "es") ? "Confirmación" : (currentLang == "ja") ? "確認" : "Confirmação";
            case 0x4b8684: return (currentLang == "en") ? "Ranking" : (currentLang == "es") ? "Clasificación" : (currentLang == "ja") ? "ランキング" : "Classificação";
            case 0x4b9bb4: return (currentLang == "en") ? "Practice Menu" : (currentLang == "es") ? "Menú de Práctica" : (currentLang == "ja") ? "プラクティスメニュー" : "Menu de Treino";
            case 0x4b9a3c: return (currentLang == "en") ? "Overlay" : (currentLang == "es") ? "Superposición" : (currentLang == "ja") ? "オーバーレイ" : "Sobreposição";
            default: return (currentLang == "en") ? "Menu" : (currentLang == "es") ? "Menú" : (currentLang == "ja") ? "メニュー" : "Menu";
        }
    }

    // ---- specialized trophy descriptions ----
    static readonly string[] TrophyNames = new string[]
    {
        "Haohmaru Completo",
        "Nakoruru Completo",
        "Hanzo Completo",
        "Galford Completo",
        "Ukyo Completo",
        "Kyoshiro Completo",
        "Genjuro Completo",
        "Basara Completo",
        "Jubei Completo",
        "Charlotte Completo",
        "Yoshitora Completo",
        "Kazuki Completo",
        "Sogetsu Completo",
        "Rera Completo",
        "4 Chefes Derrotados",
        "Espadachim (5 Vitórias Online)",
        "Mestre da Espada (20 Vitórias Online)",
        "Santo da Espada (Sequência de 10 Vitórias Online)"
    };

    static readonly string[] LeaderboardModes = new string[]
    {
        "Modo História",
        "Ranking de Vitórias",
        "Haohmaru", "Nakoruru", "Rimururu", "Hanzo", "Galford", "Kyoshiro",
        "Ukyo", "Genjuro", "Basara", "Shizumaru", "Gaira", "Amakusa",
        "Jubei", "Zankuro", "Charlotte", "Tam Tam", "Kazuki", "Sogetsu",
        "Rasetsumaru", "Rera", "Enja", "Suija", "Yoshitora", "Yunfei",
        "Mina", "Kusaregedo", "Gaoh", "Mizuki"
    };

    static string GetLeaderboardModeName(int idx)
    {
        if (idx == 0) return (currentLang == "en") ? "Story Mode" : (currentLang == "es") ? "Modo Historia" : (currentLang == "ja") ? "ストーリーモード" : "Modo História";
        if (idx == 1) return (currentLang == "en") ? "Win Ranking" : (currentLang == "es") ? "Clasificación de Victorias" : (currentLang == "ja") ? "勝利数ランキング" : "Ranking de Vitórias";
        if (idx >= 0 && idx < LeaderboardModes.Length) return LeaderboardModes[idx];
        return (currentLang == "en") ? ("Board " + (idx + 1)) : (currentLang == "es") ? ("Marcador " + (idx + 1)) : (currentLang == "ja") ? ("ボード " + (idx + 1)) : ("Placar " + (idx + 1));
    }

    static readonly string[] ControlActionKeys = new string[]
    {
        "Preset", "Default", "Up", "Down", "Left", "Right", "Start",
        "A", "B", "C", "D", "AB", "BC", "CD", "ABC", "BCD", "Coin"
    };

    static string GetControlActionName(int row)
    {
        if (currentLang == "en")
        {
            switch (row)
            {
                case 0: return "Preset";
                case 1: return "Reset to Default";
                case 2: return "Up";
                case 3: return "Down";
                case 4: return "Left";
                case 5: return "Right";
                case 6: return "Start / Pause";
                case 7: return "Light Slash (A)";
                case 8: return "Medium Slash (B)";
                case 9: return "Kick (C)";
                case 10: return "Special / Dodge (D)";
                case 11: return "Heavy Slash (A+B)";
                case 12: return "Throw (B+C)";
                case 13: return "Rage Explosion (C+D)";
                case 14: return "Honorable Suicide (A+B+C)";
                case 15: return "Overkill (B+C+D)";
                case 16: return "Insert Coin / Select";
                default: return "Control " + (row + 1);
            }
        }
        else if (currentLang == "es")
        {
            switch (row)
            {
                case 0: return "Predeterminado";
                case 1: return "Restaurar";
                case 2: return "Arriba";
                case 3: return "Abajo";
                case 4: return "Izquierda";
                case 5: return "Derecha";
                case 6: return "Start / Pausa";
                case 7: return "Corte Débil (A)";
                case 8: return "Corte Medio (B)";
                case 9: return "Patada (C)";
                case 10: return "Especial / Esquiva (D)";
                case 11: return "Corte Fuerte (A+B)";
                case 12: return "Agarre (B+C)";
                case 13: return "Explosión de Furia (C+D)";
                case 14: return "Suicidio Honorable (A+B+C)";
                case 15: return "Golpe Fatal Overkill (B+C+D)";
                case 16: return "Ficha / Seleccionar";
                default: return "Control " + (row + 1);
            }
        }
        else if (currentLang == "ja")
        {
            switch (row)
            {
                case 0: return "プリセット";
                case 1: return "初期設定に戻す";
                case 2: return "上";
                case 3: return "下";
                case 4: return "左";
                case 5: return "右";
                case 6: return "スタート / 一時停止";
                case 7: return "弱斬り (A)";
                case 8: return "中斬り (B)";
                case 9: return "蹴り (C)";
                case 10: return "特殊 (D)";
                case 11: return "強斬り (A+B)";
                case 12: return "投げ (B+C)";
                case 13: return "怒り爆発 (C+D)";
                case 14: return "自決 (A+B+C)";
                case 15: return "絶命奥義 (B+C+D)";
                case 16: return "コイン / セレクト";
                default: return "操作 " + (row + 1);
            }
        }
        else
        {
            switch (row)
            {
                case 0: return "Predefinição";
                case 1: return "Restaurar Padrão";
                case 2: return "Cima";
                case 3: return "Baixo";
                case 4: return "Esquerda";
                case 5: return "Direita";
                case 6: return "Start / Pausa";
                case 7: return "Corte Fraco (A)";
                case 8: return "Corte Médio (B)";
                case 9: return "Chute (C)";
                case 10: return "Especial / Esquiva (D)";
                case 11: return "Corte Forte (A+B)";
                case 12: return "Arremesso (B+C)";
                case 13: return "Explosão de Fúria (C+D)";
                case 14: return "Suicídio Honroso (A+B+C)";
                case 15: return "Golpe Fatal Overkill (B+C+D)";
                case 16: return "Ficha / Selecionar";
                default: return "Controle " + (row + 1);
            }
        }
    }

    // ---- Neo-Geo 68k Arcade game state ----
    const long NEO_BANK_TABLE = 0x006f4e14;   // IV: DAT_006f4e14, usada em FUN_0043dca0/FUN_0043e680
    static uint ramBase = 0;
    static int lastP1Char = -1;
    static int lastP2Char = -1;
    static int lastP1Style = -1;
    static int lastP2Style = -1;
    static int lastP1Lock = -1;
    static int lastStage = -1;
    static uint lastBg = 0xFFFFFFFF;
    static bool announcedCharSelect = false;

    // Mapeamento exato de cada um dos 29 slots de cursor do jogo (c1_obj + 0xc2)
    // Descoberto diretamente na ROM do Neo-Geo (tabela de conversão 0x1BCA2, navegação 0x1BBB8 e coordenadas 0x1BC2C):
    static readonly string[] CharacterNames = new string[]
    {
        /* Slot 00 - CharID 00 (Linha 1, Coluna 3) */ "Haohmaru",
        /* Slot 01 - CharID 01 (Linha 2, Coluna 3) */ "Nakoruru",
        /* Slot 02 - CharID 02 (Linha 2, Coluna 9) */ "Rimururu",
        /* Slot 03 - CharID 03 (Linha 3, Coluna 9) */ "Hattori Hanzo",
        /* Slot 04 - CharID 04 (Linha 3, Coluna 4) */ "Galford D. Weller",
        /* Slot 05 - CharID 05 (Linha 3, Coluna 7) */ "Kyoshiro Senryo",
        /* Slot 06 - CharID 06 (Linha 1, Coluna 9) */ "Ukyo Tachibana",
        /* Slot 07 - CharID 07 (Linha 1, Coluna 4) */ "Genjuro Kibagami",
        /* Slot 08 - CharID 08 (Linha 2, Coluna 6) */ "Basara Kubikiri",
        /* Slot 09 - CharID 09 (Linha 1, Coluna 5) */ "Shizumaru Hisame",
        /* Slot 10 - CharID 10 (Linha 1, Coluna 8) */ "Gaira Caffeine",
        /* Slot 11 - CharID 12 (Linha 3, Coluna 8) */ "Jubei Yagyu",
        /* Slot 12 - CharID 14 (Linha 3, Coluna 5) */ "Charlotte Christine de Colde",
        /* Slot 13 - CharID 15 (Linha 2, Coluna 7) */ "Tam Tam",
        /* Slot 14 - CharID 16 (Linha 1, Coluna 6) */ "Kazuki Kazama",
        /* Slot 15 - CharID 17 (Linha 1, Coluna 7) */ "Sogetsu Kazama",
        /* Slot 16 - CharID 18 (Linha 2, Coluna 8) */ "Rasetsumaru",
        /* Slot 17 - CharID 19 (Linha 2, Coluna 4) */ "Rera",
        /* Slot 18 - CharID 20 (Linha 3, Coluna 1) */ "Enja",
        /* Slot 19 - CharID 21 (Linha 3, Coluna 3) */ "Suija",
        /* Slot 20 - CharID 22 (Linha 1, Coluna 1) */ "Yoshitora Tokugawa",
        /* Slot 21 - CharID 23 (Linha 2, Coluna 5) */ "Liu Yunfei",
        /* Slot 22 - CharID 24 (Linha 2, Coluna 1) */ "Mina Majikina",
        /* Slot 23 - CharID 25 (Linha 3, Coluna 6) */ "Yokai Kusaregedo",
        /* Slot 24 - CharID 32 (Secreto / Chefe)   */ "Mizuki Rashojin",
        /* Slot 25 - CharID 11 (Linha 3, Coluna 2) */ "Shiro Tokisada Amakusa",
        /* Slot 26 - CharID 13 (Linha 2, Coluna 2) */ "Zankuro Minazuki",
        /* Slot 27 - CharID 27 (Secreto / 2x Baixo)*/ "Mizuki Rashojin",
        /* Slot 28 - CharID 26 (Linha 1, Coluna 2) */ "Gaoh (Gaoh Kyougoku Hinowa no Kami)"
    };


    // ===== SS4: nome por CHARID do jogo (medido casa a casa em 28/10) =====
    // O byte do cursor (ramBase + ((CURSOR-1 + 0xc2) ^ 1)) e o CHARID direto,
    // nao um indice de slot como no V Special. Ordem confirmada navegando a grade:
    //   linha1: 0 Haohmaru, 2 Rimururu, 1 Nakoruru, 7 Genjuro
    //   linha2: 4 Galford, 9 Shizumaru, 10 Gaira, 6 Ukyo
    //   linha3: 12 Jubei, 8 Basara, 5 Kyoshiro, 3 Hanzo
    //   linha4: 16 Kazuki, 14 Charlotte, 15 Tam Tam, 17 Sogetsu, 11 Amakusa (secreto)
    //   18 = casa aleatoria ("random")
    static readonly System.Collections.Generic.Dictionary<int, string> IV_CHAR_NAMES =
        new System.Collections.Generic.Dictionary<int, string>
    {
        { 0,  "Haohmaru" }, { 1,  "Nakoruru" }, { 2,  "Rimururu" }, { 3,  "Hanzo Hattori" },
        { 4,  "Galford" },  { 5,  "Kyoshiro Senryo" }, { 6,  "Ukyo Tachibana" }, { 7,  "Genjuro Kibagami" },
        { 8,  "Basara Kubikiri" }, { 9, "Shizumaru Hisame" }, { 10, "Caffeine Gaira" }, { 11, "Amakusa" },
        { 12, "Jubei Yagyu" }, { 13, "Zankuro Minazuki" }, { 14, "Charlotte" }, { 15, "Tam Tam" },
        { 16, "Kazuki Kazama" }, { 17, "Sogetsu Kazama" }, { 18, "aleatorio" },
    };

    // posicao na grade por CHARID -> (linha, coluna) na ordem visual
    static readonly System.Collections.Generic.Dictionary<int, string> IV_CHAR_GRID =
        new System.Collections.Generic.Dictionary<int, string>
    {
        { 0, "linha 1, coluna 1" }, { 2, "linha 1, coluna 2" }, { 1, "linha 1, coluna 3" }, { 7, "linha 1, coluna 4" },
        { 4, "linha 2, coluna 1" }, { 9, "linha 2, coluna 2" }, { 10, "linha 2, coluna 3" }, { 6, "linha 2, coluna 4" },
        { 12, "linha 3, coluna 1" }, { 8, "linha 3, coluna 2" }, { 5, "linha 3, coluna 3" }, { 3, "linha 3, coluna 4" },
        { 16, "linha 4, coluna 1" }, { 14, "linha 4, coluna 2" }, { 15, "linha 4, coluna 3" },
        { 17, "linha 4, coluna 4" }, { 11, "linha 4, coluna 5, secreto" },
        { 18, "linha 4, coluna 6, aleatorio" },
    };

    static string IvCharName(int id)
    {
        string n;
        if (IV_CHAR_NAMES.TryGetValue(id, out n)) return n;
        return "";
    }
    static string IvCharGrid(int id)
    {
        string g;
        if (IV_CHAR_GRID.TryGetValue(id, out g)) return g;
        return "";
    }

    static readonly byte[] SlotToCharIdTable = new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,
        0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x20, 0x0B, 0x0D, 0x1B, 0x1A
    };

    static int CharIdToSlot(byte charId)
    {
        for (int i = 0; i < SlotToCharIdTable.Length; i++)
        {
            if (SlotToCharIdTable[i] == charId) return i;
        }
        return -1;
    }

    static string GetStageName(int stage)
    {
        string pfx = (currentLang == "en") ? "Stage " : (currentLang == "es") ? "Escenario " : (currentLang == "ja") ? "ステージ " : "Cenário ";
        if (stage >= 0 && stage <= 5) return pfx + (stage + 1);
        if (stage == 6) return (currentLang == "en") ? "Amakusa Stage" : (currentLang == "es") ? "Escenario Amakusa" : (currentLang == "ja") ? "天草ステージ" : "Cenário Amakusa";
        if (stage == 7) return (currentLang == "en") ? "Zankuro Stage" : (currentLang == "es") ? "Escenario Zankuro" : (currentLang == "ja") ? "斬紅郎ステージ" : "Cenário Zankuro";
        if (stage == 8) return (currentLang == "en") ? "Gaoh Stage" : (currentLang == "es") ? "Escenario Gaoh" : (currentLang == "ja") ? "我旺ステージ" : "Cenário Gaoh";
        if (stage == 9) return (currentLang == "en") ? "Final Mizuki Stage" : (currentLang == "es") ? "Escenario Final Mizuki" : (currentLang == "ja") ? "最終水姫ステージ" : "Cenário Final Mizuki";
        return pfx + (stage + 1);
    }

    static string GetBackgroundName(uint bg)
    {
        uint id = bg & 0xFFFF;
        if (currentLang == "en")
        {
            switch (id)
            {
                case 0x0000: return "Pier (Late Afternoon)";
                case 0x0001: return "Pier (Night)";
                case 0x0002: return "Bridge with People";
                case 0x0003: return "Bridge with Dog";
                case 0x0004: return "Shrine";
                case 0x0005: return "Shrine (Night)";
                case 0x0008: return "Obama Beach";
                case 0x0009: return "Dohsaki (Zankuro)";
                case 0x000A: return "Kusenbutake (Amakusa)";
                case 0x000B: return "Yoshitora Mansion";
                case 0x000C: return "Mountains";
                case 0x000D: return "Mina Shrine (Night)";
                case 0x000E: return "Graveyard";
                case 0x0012: return "Aftermath (Gaoh)";
                case 0x0013: return "Evil Shrine of Mizuki";
                default: return null;
            }
        }
        else if (currentLang == "es")
        {
            switch (id)
            {
                case 0x0000: return "Muelle (Atardecer)";
                case 0x0001: return "Muelle (Noche)";
                case 0x0002: return "Puente con Gente";
                case 0x0003: return "Puente con Perro";
                case 0x0004: return "En el Templo";
                case 0x0005: return "En el Templo (Noche)";
                case 0x0008: return "Playa de Obama";
                case 0x0009: return "Dohsaki (Zankuro)";
                case 0x000A: return "Kusenbutake (Amakusa)";
                case 0x000B: return "Mansión Yoshitora";
                case 0x000C: return "Montañas";
                case 0x000D: return "Templo de Mina (Noche)";
                case 0x000E: return "Cementerio";
                case 0x0012: return "Posguerra (Gaoh)";
                case 0x0013: return "Templo del Mal de Mizuki";
                default: return null;
            }
        }
        else if (currentLang == "ja")
        {
            switch (id)
            {
                case 0x0000: return "波止場 (夕方)";
                case 0x0001: return "波止場 (夜)";
                case 0x0002: return "橋 (人々)";
                case 0x0003: return "橋 (犬)";
                case 0x0004: return "神社";
                case 0x0005: return "神社 (夜)";
                case 0x0008: return "小浜海岸";
                case 0x0009: return "堂崎 (斬紅郎)";
                case 0x000A: return "九千部岳 (天草)";
                case 0x000B: return "慶寅邸";
                case 0x000C: return "連山";
                case 0x000D: return "ミナ神社 (夜)";
                case 0x000E: return "墓場";
                case 0x0012: return "戦跡 (我旺)";
                case 0x0013: return "恐山 (水姫)";
                default: return null;
            }
        }
        else
        {
            switch (id)
            {
                case 0x0000: return "Cais (Fim de Tarde)";
                case 0x0001: return "Cais (Noite)";
                case 0x0002: return "Ponte com Pessoas";
                case 0x0003: return "Ponte com Cachorro";
                case 0x0004: return "No Templo";
                case 0x0005: return "No Templo (Noite)";
                case 0x0008: return "Praia de Obama";
                case 0x0009: return "Dohsaki (Zankuro)";
                case 0x000A: return "Kusenbutake (Amakusa)";
                case 0x000B: return "Mansão Yoshitora";
                case 0x000C: return "Montanhas";
                case 0x000D: return "Templo de Mina (Noite)";
                case 0x000E: return "Cemitério";
                case 0x0012: return "Pós-Guerra (Gaoh)";
                case 0x0013: return "Templo do Mal de Mizuki";
                default: return null;
            }
        }
    }

    static int FindCursorObject(IntPtr h, long ramBase, string targetTag)
    {
        ushort headOffset = ReadU16(h, ramBase + 4);
        long curr = 0x100000 + headOffset;
        int count = 0;
        while (curr >= 0x100000 && curr <= 0x10ffff && (curr & 1) == 0 && count < 64)
        {
            byte[] chunk = ReadBytes(h, ramBase + (curr & 0xffff) + 0x10, 16);
            if (chunk != null && chunk.Length >= 8)
            {
                char[] tagChars = new char[8];
                for (int i = 0; i < 8; i++) tagChars[i] = (char)chunk[i ^ 1];
                string tag = new string(tagChars);
                if (tag.StartsWith(targetTag))
                {
                    return (int)(curr & 0xffff);
                }
            }
            ushort nextOffset = ReadU16(h, ramBase + ((curr + 4) & 0xffff));
            if (nextOffset == 0 || nextOffset == headOffset) break;
            curr = 0x100000 + nextOffset;
            count++;
        }
        return -1;
    }

    static bool PollNeoGeoGame(IntPtr h, bool force)
    {
        if (ramBase == 0)
        {
            long pNeo = dynamicNeoBankTable != 0 ? dynamicNeoBankTable : ResolveAddr(NEO_BANK_TABLE);
            ramBase = ReadU32(h, pNeo + 0x400 * 4);   // IV: work RAM 68k = 0x100000, tabela indexada por addr>>10
            if (ramBase < 0x10000 || ramBase > 0x7fff0000)
            {
                ramBase = 0;
                return false;
            }
            Log("Neo-Geo RAM Base descoberto: 0x" + ramBase.ToString("X"));
        }

        // Live cursor discovery via Neo-Geo object linked list
        int c1_obj = FindCursorObject(h, ramBase, "CURSOR-1");
        int c2_obj = FindCursorObject(h, ramBase, "CURSOR-2");

        ushort phase = ReadU16(h, ramBase + 0x80c4);
        // MEDIDO (28/10, 170 frames da tela de selecao gravados em tools/cs_rec.bin):
        // a fase da selecao de personagem e 0x0008 e nela a lista de objetos traz
        // CURSOR-1 no slot 0x600 e CURSOR-2 no slot 0x700 (170/170 frames).
        // 0x0800 e a mesma tela com o bit alto ligado (visto em frame 6200).
        // 0x0009 (VS) e 0x000F (apresentacao) tambem passam: e nelas que o byte do
        // ESTILO e do NIVEL fica valido (medido 28/10 com o jogador).
        bool isVsOrIntro = (phase == 0x0009 || phase == 0x000F);
        // ATENCAO: em 0x0009/0x000F a lista de objetos AINDA tem CURSOR-1/CURSOR-2.
        // Se isCharSelect considerar o cursor, o bloco da selecao roda e faz "return",
        // e o estilo/nivel nunca sao alcancados. Por isso o cursor so conta como
        // selecao quando NAO estamos em fase de VS/apresentacao.
        bool isCharSelect = (phase == 0x0008 || phase == 0x0808)
                            || (!isVsOrIntro && (c1_obj != -1 || c2_obj != -1));
        bool isStageSelect = (phase == 2 || phase == 0x0200 || phase == 3 || phase == 0x0300);

        // 0x0009 (VS) e 0x000F (apresentacao da luta) tambem precisam passar:
        // e nelas que o byte do ESTILO fica valido (0=Shura,1=Rasetsu), medido 28/10.
        if (!isCharSelect && !isStageSelect && !isVsOrIntro)
        {
            announcedCharSelect = false;
            lastP1Char = -1;
            lastP2Char = -1;
            lastP1Style = -1;
            lastP2Style = -1;
            lastP1Lock = -1;
            return false;
        }

        bool spoke = false;

        string p1Word = (currentLang == "en") ? "Player 1" : (currentLang == "es") ? "Jugador 1" : (currentLang == "ja") ? "プレイヤー1" : "Jogador 1";
        string p2Word = (currentLang == "en") ? "Player 2" : (currentLang == "es") ? "Jugador 2" : (currentLang == "ja") ? "プレイヤー2" : "Jogador 2";
        string charSelectTitle = (currentLang == "en") ? "Character Selection" : (currentLang == "es") ? "Selección de Personajes" : (currentLang == "ja") ? "キャラクター選択" : "Seleção de Personagens";
        string charWord = (currentLang == "en") ? "Character" : (currentLang == "es") ? "Personaje" : (currentLang == "ja") ? "キャラクター" : "Personagem";
        string styleWord = (currentLang == "en") ? "Style" : (currentLang == "es") ? "Estilo" : (currentLang == "ja") ? "修羅・羅刹" : "Estilo";
        string selWord = (currentLang == "en") ? "Selected" : (currentLang == "es") ? "Seleccionado" : (currentLang == "ja") ? "決定" : "Selecionado";
        string stageWord = (currentLang == "en") ? "Stage: " : (currentLang == "es") ? "Escenario: " : (currentLang == "ja") ? "ステージ: " : "Cenário: ";

        // ===== Character Select (SS4) — MEDIDO em 28/10 =====
        // O personagem sob o cursor sai de ramBase + ((CURSOR-1 + 0xc2) ^ 1),
        // a MESMA tecnica do narrador do V Special. O valor e o CHARID do jogo.
        if (isCharSelect)
        {
            int p1Char = -1, p2Char = -1;
            if (c1_obj != -1) p1Char = ReadU8(h, ramBase + (((c1_obj + 0xc2) ^ 1) & 0xffff));
            if (c2_obj != -1) p2Char = ReadU8(h, ramBase + (((c2_obj + 0xc2) ^ 1) & 0xffff));

            bool mudou = (p1Char != lastP1Char) || (p2Char != lastP2Char);

            // armados ao passar pela selecao; cada campo tem o SEU gatilho
            styleArmedP1 = true; styleArmedP2 = true; nivelArmed = true; nivelArmedP2 = true;
            // PEDIDO DO JOGADOR (28/10): falar SO O NOME do personagem.
            // Sem "Jogador 1", sem "linha 1, coluna 2", sem posicao.
            if (!announcedCharSelect || force)
            {
                announcedCharSelect = true;
                string n1 = IvCharName(p1Char);
                string n2i = (p2Char >= 0) ? IvCharName(p2Char) : "";
                if (n1 != "") Speak(n1 + ".");
                else if (n2i != "") Speak(n2i + ".");
                else Speak(charSelectTitle + ".");
                lastP1Char = p1Char; lastP2Char = p2Char;
                Log(string.Format("[dbg] ivselect TELA phase=0x{0:X4} c1={1} c2={2} p1id={3} nome1='{4}'",
                    phase, c1_obj, c2_obj, p1Char, n1));
                return true;
            }

            if (mudou)
            {
                bool p1Mudou = (p1Char != -1 && p1Char != lastP1Char);
                bool p2Mudou = (p2Char != -1 && p2Char != lastP2Char);
                lastP1Char = p1Char; lastP2Char = p2Char;

                string n1 = (p1Char >= 0) ? IvCharName(p1Char) : "";
                string n2 = (p2Char >= 0) ? IvCharName(p2Char) : "";

                if (p1Mudou && n1 != "")
                {
                    string fala = n1;
                    if (p1Char == 11) fala += ", secreto";
                    if (p1Char == 18) fala += ", aleatorio";
                    // Em 2 jogadores, identifica o lado (o jogador 2 fica na direita).
                    if (c2_obj != -1) fala = p1Word + ", " + fala;
                    Speak(fala);
                    Log(string.Format("[dbg] ivselect P1 id={0} nome='{1}'", p1Char, n1));
                    spoke = true;
                }
                if (p2Mudou && n2 != "")
                {
                    string fala2 = n2;
                    if (p2Char == 11) fala2 += ", secreto";
                    if (p2Char == 18) fala2 += ", aleatorio";
                    if (c1_obj != -1) fala2 = p2Word + ", " + fala2;
                    Speak(fala2);
                    Log(string.Format("[dbg] ivselect P2 id={0} nome='{1}'", p2Char, n2));
                    spoke = true;
                }
            }
            return spoke;
        }

        // ===== NIVEL / CLASSE DE LUTA (SS4) — MEDIDO em 28/10 =====
        // 0x8327 = nivel escolhido. Valores medidos com o jogador:
        //   1 = primeiro da tela (Iniciante)   -> nome "Iniciante"
        //   0 = segundo  da tela (Medio)       -> nome "Medio"
        //   2 = terceiro da tela (Avancado)    -> nome "Avancado"
        // O indice NAO segue a ordem da tela (mesmo padrao do estilo/personagem).
        if ((phase == 0x0009 || phase == 0x000F) && nivelArmed)
        {
            int niv = ReadU8(h, ramBase + 0x8327);
            if (niv >= 0 && niv <= 2)
            {
                if (niv != candNivel) { candNivel = niv; candNivelCount = 0; }
                else candNivelCount++;
                if (candNivelCount >= 2 && niv != lastNivel)
                {
                    lastNivel = niv;
                    string nomeNiv = (niv == 1) ? ((currentLang == "en") ? "Beginner" : "Iniciante")
                                   : (niv == 0) ? ((currentLang == "en") ? "Medium" : "Medio")
                                   : ((currentLang == "en") ? "Advanced" : "Avancado");
                    Speak(nomeNiv);
                    nivelArmed = false;
                    Log(string.Format("[dbg] ivnivel valor={0} nome='{1}' fase=0x{2:X4}", niv, nomeNiv, phase));
                    spoke = true;
                }
            }
            else { candNivel = -1; candNivelCount = 0; }
        }

        // ===== ESTILO e NIVEL (SS4) — MEDIDO em 28/10 =====
        // Estilo: 0x8325 = P1, 0x8324 = P2.  0 = Shura, 1 = Rasetsu.
        // Nivel:  0x8327.  1 = Iniciante, 0 = Medio, 2 = Avancado (ordem interna).
        // Fatos medidos, que explicam os bugs anteriores:
        //  - na NAVEGACAO esses bytes tem lixo (159/255/78); o valor real so aparece
        //    na confirmacao (0x0009 -> 0x000F);
        //  - o byte FICA CONGELADO depois (menu de pausa, luta) => nao se pode falar
        //    so porque mudou: repetia "Shura" no menu;
        //  - os gatilhos precisam ser SEPARADOS por jogador e por campo, senao o P1
        //    desarma o do P2 e o nivel nunca chega a falar.
        if (phase == 0x0009 || phase == 0x000F)
        {
            int est1 = ReadU8(h, ramBase + 0x8325);
            int est2 = ReadU8(h, ramBase + 0x8324);
            int niv1 = ReadU8(h, ramBase + 0x8327);

            string nomeEstilo1 = (est1 == 0) ? "Shura" : "Rasetsu";
            string nomeEstilo2 = (est2 == 0) ? "Shura" : "Rasetsu";
            string nomeNiv = (niv1 == 1) ? "Iniciante" : (niv1 == 0) ? "Medio" : "Avancado";

            // --- P1 estilo ---
            if (est1 >= 0 && est1 <= 1)
            {
                if (est1 != candEst1) { candEst1 = est1; candEst1Count = 0; } else candEst1Count++;
                if (styleArmedP1 && candEst1Count >= 2 && est1 != lastP1Style)
                {
                    lastP1Style = est1;
                    styleArmedP1 = false;
                    Speak((c2_obj != -1) ? (p1Word + ", " + nomeEstilo1) : nomeEstilo1);
                    Log(string.Format("[dbg] ivstyle P1={0} '{1}' fase=0x{2:X4}", est1, nomeEstilo1, phase));
                    spoke = true;
                }
            }
            else { candEst1 = -1; candEst1Count = 0; }

            // --- P2 estilo ---
            // NAO exigir c2_obj != -1: na fase de VS (0x0009) a lista de objetos ja
            // perdeu o CURSOR-2, mas o byte do estilo continua valido (medido 28/10:
            // est2=1 em 0x0009 com a lista sem cursor). Exigir o cursor aqui era o
            // motivo do P2 nunca falar o estilo em 2 jogadores.
            // Basta o byte estar na faixa valida (0..1). No modo de 1 jogador ele fica
            // em 255 (nao preenchido), entao nao ha falso positivo.
            bool temP2 = (est2 >= 0 && est2 <= 1);
            if (temP2)
            {
                if (est2 != candEst2) { candEst2 = est2; candEst2Count = 0; } else candEst2Count++;
                if (styleArmedP2 && candEst2Count >= 2 && est2 != lastP2Style)
                {
                    lastP2Style = est2;
                    styleArmedP2 = false;
                    Speak((c1_obj != -1) ? (p2Word + ", " + nomeEstilo2) : nomeEstilo2);
                    Log(string.Format("[dbg] ivstyle P2={0} '{1}' fase=0x{2:X4}", est2, nomeEstilo2, phase));
                    spoke = true;
                }
            }

            // --- nivel do P2: 0x8326 (medido 28/10, fica ao lado do 0x8327 do P1,
            //     no mesmo padrao dos estilos 0x8325=P1 / 0x8324=P2) ---
            int niv2 = ReadU8(h, ramBase + 0x8326);
            if (niv2 >= 0 && niv2 <= 2 && (c2_obj != -1 || lastP2Char != -1 || niv2 != niv1))
            {
                if (niv2 != candNivel2) { candNivel2 = niv2; candNivel2Count = 0; } else candNivel2Count++;
                if (nivelArmedP2 && candNivel2Count >= 2 && niv2 != lastNivel2)
                {
                    lastNivel2 = niv2;
                    nivelArmedP2 = false;
                    string nn2 = (niv2 == 1) ? "Iniciante" : (niv2 == 0) ? "Medio" : "Avancado";
                    Speak((c1_obj != -1 || lastP1Char != -1) ? (p2Word + ", " + nn2) : nn2);
                    Log(string.Format("[dbg] ivnivel P2 valor={0} '{1}' fase=0x{2:X4}", niv2, nn2, phase));
                    spoke = true;
                }
            }
            else { candNivel2 = -1; candNivel2Count = 0; }

            // --- nivel do P1 (0x8327) ---
            if (niv1 >= 0 && niv1 <= 2)
            {
                if (niv1 != candNivel) { candNivel = niv1; candNivelCount = 0; } else candNivelCount++;
                if (nivelArmed && candNivelCount >= 2 && niv1 != lastNivel)
                {
                    lastNivel = niv1;
                    nivelArmed = false;
                    Speak((c2_obj != -1 || lastP2Char != -1) ? (p1Word + ", " + nomeNiv) : nomeNiv);
                    Log(string.Format("[dbg] ivnivel P1 valor={0} '{1}' fase=0x{2:X4}", niv1, nomeNiv, phase));
                    spoke = true;
                }
            }
            else { candNivel = -1; candNivelCount = 0; }
        }

        // Pre-battle / Stage Select
        // DESLIGADO a 28/10: estes offsets sao HIPOTESE do V Special, nao medidos no IV.
        // Ao consertar o portao do Poll, este bloco passou a rodar e falou "Cenario: Cais"
        // repetidamente em fases de luta (0x0002/0x0003), porque 0x8b4c oscila quadro a
        // quadro (00 0C 6E 00 -> 00 00 6E 00 -> 00 04 6E 00) e nao e um ID de palco.
        // Falar palco errado e pior que silencio -> fica desligado ate ser medido.
        if (false && isStageSelect)
        {
            byte stage = ReadU8(h, ramBase + 0x836d);
            if (stage != lastStage && stage <= 9)
            {
                lastStage = stage;
                string stName = GetStageName(stage);
                Speak(stageWord + stName);
                spoke = true;
            }

            uint bg = ReadU32(h, ramBase + 0x8b4c);
            if (bg != lastBg)
            {
                lastBg = bg;
                string bgName = GetBackgroundName(bg);
                if (bgName != null)
                {
                    Speak(stageWord + bgName);
                    spoke = true;
                }
            }
        }

        return spoke;
    }

    // ---- polling ----
    // ---- polling ----
    static bool Poll(bool force)
    {
        if (hProc == IntPtr.Zero) return false;

        // 0. Idioma do MOD acompanha o idioma do JOGO.
        // IV: o idioma atual e a opcao de Setting ID 'L' (getter do motor FUN_00482ab0),
        // que devolve o INDICE: 0=ja, 1=en, 2=es, 3=pt (ordem lida do Options.csv).
        // No V Special era um ponteiro de string em 0x005e85d0; no IV esse endereco nao vale.
        int ivLangIdx = IvOptionValue(hProc, 'L');
        if (ivLangIdx >= 0 && ivLangIdx != lastIvLangIdx)
        {
            string[] ivLangCodes = new string[] { "ja", "en", "es", "pt" };
            if (ivLangIdx < ivLangCodes.Length)
            {
                string detectedLang = ivLangCodes[ivLangIdx];
                bool primeiraLeitura = (lastIvLangIdx == -1);
                lastIvLangIdx = ivLangIdx;
                // Na PRIMEIRA leitura, adota o idioma do jogo sem falar nada
                // (antes isso era ignorado, entao o mod ficava em pt com o jogo em en).
                if (primeiraLeitura)
                {
                    if (detectedLang != currentLang)
                    {
                        SetLanguage(detectedLang);
                        lastDedup = null;
                        Log("[lang] idioma inicial adotado do jogo: " + detectedLang);
                    }
                }
                else if (detectedLang != currentLang)
                {
                    SetLanguage(detectedLang);
                    lastDedup = null; // forca reanunciar o menu ativo no idioma novo
                    string langName = (detectedLang == "en") ? "Language: English" :
                                      (detectedLang == "es") ? "Idioma: Español" :
                                      (detectedLang == "ja") ? "言語: 日本語" : "Idioma: Português";
                    Speak(langName);
                }
            }
        }
        else if (ivLangIdx == -1)
        {
            // jogo sem cadeia de opcoes (tela inicial): mantem o idioma atual
        }

        // 1. First priority: front-end UI menus (MainMenu, PauseMenu, Options, SkillList, Settings, etc.)
        mgr = GetManager(hProc);
        if (mgr != 0)
        {
            uint menu = ReadU32(hProc, mgr + OFF_MGR_MENU);
            if (menu >= 0x10000 && menu <= 0x7fff0000)
            {
                uint vt = ReadU32(hProc, menu);
                uint origVt = (uint)(vt - baseDelta);
                // IV: o "jogo rodando" NAO e o PlayMenu do V Special (0x4b8424, que no IV
                // nem existe). Medido ao vivo 28/10: durante a emulacao o menu ativo e
                // PlayPopupMenu (0x0053C634) e vem com lista de nos vazia -> o PollMenu
                // nao tinha nada para falar e o bloco Neo-Geo nunca era alcancado.
                // Prova: menuwatch.py -> vt=0x0053C634, node='' durante fase 0x0008/0x0200.
                if (origVt != IVVT_PlayPopupMenu)
                {
                    // Reset character select tracking whenever the user is in a front-end menu
                    announcedCharSelect = false;
                    lastP1Char = -1;
                    lastP2Char = -1;
                    lastP1Style = -1;
                    lastP2Style = -1;
                    lastP1Lock = -1;

                    // STRICT: Return front-end menu poll. NEVER fall through to Neo-Geo RAM while in a menu!
                    return PollMenu(hProc, menu, origVt, force);
                }
            }
        }
        // 2. Second priority: arcade emulation (menu ativo = PlayPopupMenu, 0x0053C634 no IV)
        return PollNeoGeoGame(hProc, force);
    }

    static int GetEngineOptionValue(IntPtr h, byte rowType)
    {
        if (rowType == 0 || rowType == (byte)' ') return -1;
        uint optBuf = ReadU32(h, ResolveAddr(0x0053e9e0));
        if (optBuf < 0x10000 || optBuf > 0x7fff0000)
            optBuf = ReadU32(h, ResolveAddr(0x005e00a0));
        if (optBuf < 0x10000 || optBuf > 0x7fff0000)
            optBuf = ReadU32(h, ResolveAddr(0x0051d030));
        if (optBuf < 0x10000 || optBuf > 0x7fff0000)
            optBuf = (uint)ResolveAddr(0x004b4668);

        byte[] str = ReadBytes(h, optBuf, 128);
        if (str != null)
        {
            for (int i = 0; i + 1 < str.Length && str[i] != 0; i += 2)
            {
                if (str[i] == rowType)
                {
                    byte valChar = str[i + 1];
                    if (valChar >= (byte)'0' && valChar <= (byte)'9')
                        return valChar - (byte)'0';
                    if (valChar == (byte)':')
                        return 10;
                    if (valChar >= (byte)'A' && valChar <= (byte)'Z')
                        return valChar - (byte)'A' + 10;
                    return valChar - (byte)'0';
                }
            }
        }
        return -1;
    }

    // ---- IV: leitura do valor atual das opcoes ----
    // Espelho exato de FUN_00482ab0: procura o par <id><digito> na copia viva (+0x631B44),
    // depois nas opcoes do usuario (+0x631B40), depois nos padroes (+0x631B24) e devolve o
    // digito. -1 = "nao sei": o chamador NAO fala valor quando recebe -1.
    static int IvOptionValue(IntPtr h, char id)
    {
        int v = IvOptionValueIn(h, ReadU32(h, ResolveAddr(IV_OPT_LIVE)), id, -1);
        if (v >= 0) return v;
        v = IvOptionValueIn(h, ReadU32(h, ResolveAddr(IV_OPT_USER)), id, -1);
        if (v >= 0) return v;
        return IvOptionValueIn(h, ReadU32(h, ResolveAddr(IV_OPT_DEFAULT)), id, -1);
    }

    static int IvOptionValueIn(IntPtr h, uint p, char id, int fallback)
    {
        if (p < 0x00010000 || p > 0x7fff0000) return fallback;
        byte[] b = ReadBytes(h, p, 160);
        if (b == null) return fallback;
        for (int i = 0; i + 1 < b.Length && b[i] != 0 && b[i + 1] != 0; i += 2)
        {
            if (b[i] == (byte)id)
            {
                byte d = b[i + 1];
                return (d >= (byte)'0' && d <= (byte)'9') ? d - (byte)'0' : -1;
            }
        }
        return fallback;
    }

    // Dump cru da cadeia, so para o log -Verbose (prova do que foi lido).
    static string IvOptionChainDump(IntPtr h)
    {
        StringBuilder sb = new StringBuilder();
        string[] nm = new string[] { "live", "user", "default" };
        uint[] ad = new uint[] { IV_OPT_LIVE, IV_OPT_USER, IV_OPT_DEFAULT };
        for (int i = 0; i < 3; i++)
        {
            uint p = ReadU32(h, ResolveAddr(ad[i]));
            string s = (p >= 0x00010000 && p <= 0x7fff0000) ? ReadCString(h, p, 120) : "null";
            sb.Append(nm[i]).Append("@0x").Append(p.ToString("x")).Append("=").Append(s).Append(" ; ");
        }
        return sb.ToString();
    }

    // Texto de uma escolha, espelhando FUN_004760c0 (a funcao com que o motor rotula a escolha
    // na tela): usa a traducao da tabela de strings; se a chave nao estiver la, descarta o
    // prefixo "option_" SOMENTE quando o resto e feito de digitos, ',' e ':' (e o caso de
    // option_1, option_60, option_1:1). Fora disso devolve "" - o narrador nunca fala
    // identificador cru de chave.
    static string IvChoiceText(IntPtr h, uint choiceKeys, int idx)
    {
        uint ck = ReadU32(h, (long)choiceKeys + (long)idx * 4);
        if (ck < 0x00010000 || ck > 0x7fff0000) return "";
        string k = ReadCString(h, ck, 64);
        if (!IsPrintableKey(k)) return "";
        if (stringMap.ContainsKey(k))
        {
            string t = stringMap[k];
            return (t != null) ? t : "";
        }
        if (!k.StartsWith("option_")) return "";
        string rest = k.Substring(7);
        if (rest.Length == 0) return "";
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            bool ok = (c >= '0' && c <= '9') || c == ',' || c == ':';
            if (c == 's' && i == rest.Length - 1) ok = true;   // caso "option_s" do motor
            if (!ok) return "";
        }
        return rest;
    }

    // Chave CRUA da escolha no indice idx (o que o motor traduz com FUN_004760c0). Serve para
    // dois fins: (a) reconhecer a linha de IDIOMA (option_ja/en/es/pt); (b) gravar no log a
    // prova da conferencia dos booleanos: id -> valor -> chave DAQUELA linha -> texto.
    static string IvChoiceKey(IntPtr h, uint choiceKeys, int idx)
    {
        if (choiceKeys < 0x00010000 || choiceKeys > 0x7fff0000 || idx < 0) return "";
        uint ck = ReadU32(h, (long)choiceKeys + (long)idx * 4);
        if (ck < 0x00010000 || ck > 0x7fff0000) return "";
        string k = ReadCString(h, ck, 64);
        return IsPrintableKey(k) ? k : "";
    }

    // Lista de idiomas do PROPRIO MOTOR: [0x00631B28] = char*[] {"ja","en","es","pt"} e
    // [0x00631B2C] = quantidade. Medido no desmonte: FUN_004740c0 0x0047461f e 0x004745b6
    // (monta o array a partir das colunas Choice da linha de Setting ID 'L' do Options.csv).
    // FUN_00483e70 usa FUN_00482ab0(0x4c) para indexar esse array e carregar a tabela de
    // strings do idioma -> logo e ESTE array a "lista de idiomas" do motor.
    static string IvLanguageCodes(IntPtr h)
    {
        int n = (int)ReadU32(h, ResolveAddr(0x00631B2C));
        uint arr = ReadU32(h, ResolveAddr(0x00631B28));
        if (n <= 0 || n > 16 || arr < 0x00010000 || arr > 0x7fff0000) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            uint p = ReadU32(h, (long)arr + i * 4);
            string c = (p >= 0x00010000) ? ReadCString(h, p, 8) : "";
            if (i > 0) sb.Append(",");
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Nome do idioma NO IDIOMA DO NARRADOR (pt), a partir do codigo do motor. Usado para que o
    // nome falado nao dependa de a tela estar no idioma que o jogador nao le.
    static string IvLanguageNameByCode(string code)
    {
        switch (code)
        {
            case "ja": return L4("Japanese", "Japonés", "日本語", "Japonês");
            case "en": return L4("English", "Inglés", "英語", "Inglês");
            case "es": return L4("Spanish", "Español", "スペイン語", "Espanhol");
            case "pt": return L4("Portuguese", "Portugués", "ポルトガル語", "Português");
            default: return "";
        }
    }

    // Traduz um codigo de idioma do motor ("ja") para o nome falado. Se nao for um codigo
    // conhecido devolve "" (o chamador cala a boca em vez de falar codigo cru).
    static string IvLanguageName(IntPtr h, int idx)
    {
        string codes = IvLanguageCodes(h);
        if (codes.Length == 0) return "";
        string[] parts = codes.Split(',');
        if (idx < 0 || idx >= parts.Length) return "";
        return IvLanguageNameByCode(parts[idx]);
    }

    // Lista TODA a lista de idiomas do motor, com o nome de cada um no idioma do narrador.
    static string IvLanguageListFromEngine(IntPtr h)
    {
        string codes = IvLanguageCodes(h);
        if (codes.Length == 0) return "";
        string[] parts = codes.Split(',');
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            string nm = IvLanguageNameByCode(parts[i]);
            if (nm.Length == 0) return "";
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(nm);
        }
        return sb.ToString();
    }

    // Lista das linguas disponiveis na linha do idioma (chaves option_ja/en/es/pt da propria
    // linha), traduzidas. Usada ao ENTRAR na tela de idioma.
    static string IvLanguageList(IntPtr h, long menu, int idx)
    {
        // Fonte 1 (autoritativa): o array do proprio motor, [0x631B28]/[0x631B2C].
        string eng = IvLanguageListFromEngine(h);
        if (eng.Length > 0) return eng;
        // Fonte 2: as chaves de escolha da linha.
        uint itemsArr = ReadU32(h, menu + IV_OFF_SET_ITEMS);
        if (itemsArr < 0x00010000 || itemsArr > 0x7fff0000) return "";
        long itemPtr = (long)itemsArr + (long)idx * 20;
        uint ck = ReadU32(h, itemPtr + 0);
        int nc = (int)ReadU32(h, itemPtr + 4);
        if (ck < 0x00010000 || nc < 2 || nc > 16) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < nc; i++)
        {
            uint kp = ReadU32(h, (long)ck + (long)i * 4);
            if (kp < 0x00010000) return "";
            string k = ReadCString(h, kp, 48);
            if (!stringMap.ContainsKey(k)) return "";
            string t = stringMap[k];
            if (t == null || t.Length == 0) return "";
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    // Chave de idioma do motor (option_ja/option_en/option_es/option_pt) - usada para nomear
    // uma eventual tela de lista de idiomas que nao seja o SettingsMenu.
    static bool IsLanguageKey(string k)
    {
        return k == "option_ja" || k == "option_en" || k == "option_es" || k == "option_pt";
    }

    static string LanguageTitle()
    {
        return L4("Language", "Idioma", "言語", "Idioma");
    }

    static string LanguageChoicesWord()
    {
        return L4("Languages", "Idiomas", "言語", "Idiomas");
    }

    // ---- IV: ControlSettingsMenu row labels ----
    // The engine's row keys come from its own static table (0x00577C50) and are translated with the
    // very same strings.txt the engine loads. The five direction rows do NOT translate to Unicode
    // arrows in that file - they are raw font glyph codes (0x18..0x1B) - so they are named in words
    // here; every other row uses the engine text when it is clean, and never an untranslated key.
    static string ControlRowLabel(string key)
    {
        switch (key)
        {
            case "arrow_up":    return L4("Up", "Arriba", "上", "Cima");
            case "arrow_down":  return L4("Down", "Abajo", "下", "Baixo");
            case "arrow_left":  return L4("Left", "Izquierda", "左", "Esquerda");
            case "arrow_right": return L4("Right", "Derecha", "右", "Direita");
            // A linha 'start' e o botao Start; o proprio strings.txt do jogo traduz a chave
            // 'start' como "Menu de Pausa" (descuido de localizacao: e a string do menu de pausa),
            // o que confundiria o jogador. A linha e o Start, entao o narrador fala "Start"
            // (o texto cru do motor fica no log do modo -Verbose).
            case "start":       return L4("Start", "Start", "スタート", "Start");
        }
        if (stringMap.ContainsKey(key))
        {
            string t = stringMap[key];
            bool clean = t != null && t.Length > 0;
            for (int i = 0; i < t.Length && clean; i++) if (t[i] < 32) clean = false;
            if (clean) return t;
        }
        return "";
    }

    // ---- IV: VALORES das linhas da tela de Controles ----
    // Tudo abaixo sai dos DADOS do mapeamento (nao do texto do no, que vira textura) e segue
    // exatamente o codigo do motor em FUN_0045e670. Ver os comentarios dos consts IV_CTRL_*.

    // texto do strings.txt (pt) para uma chave do motor, ja filtrado
    static string CtrlText(string key)
    {
        if (key == null || key.Length == 0) return "";
        if (!stringMap.ContainsKey(key)) return "";
        string t = stringMap[key];
        if (t == null || t.Length == 0) return "";
        for (int i = 0; i < t.Length; i++) if (t[i] < 32) return "";
        return t;
    }

    // sprintf da string do motor quando ela tem %d (ex.: "button_generic" = "Botao %d")
    static string CtrlFmt(string key, int n, string fallback)
    {
        string t = CtrlText(key);
        if (t.Length > 0 && t.Contains("%d")) return t.Replace("%d", n.ToString());
        if (t.Length > 0 && !t.Contains("%")) return t;
        return fallback + " " + n;
    }

    static int CtrlMenuId(uint menu) { return (int)ReadU32(hProc, menu + IV_OFF_CTRL_MENUID); }

    // Linhas realmente montadas pelo motor: (id!=0x2E)+0x10-((~flags>>1)&1).
    // MEDIDO ao vivo (28/10): id=6 e *(u32*)(*(0x714E34)+8)=1 -> 16 linhas
    // (0=Dispositivo ... 15=Continuar; a linha "Add Coin" nao existe nesta variante).
    static int CtrlRowTotal(int menuId)
    {
        int baseRows = (menuId == 0x2e) ? 16 : 17;
        uint p = ReadU32(hProc, ResolveAddr(IV_CTRL_FLAG_PTR));
        int cut = 0;
        if (p >= 0x10000 && p <= 0x7fff0000)
            cut = (int)((~ReadU32(hProc, (long)p + 8) >> 1) & 1);
        if (cut != 0 && cut != 1) cut = 0;
        return baseRows - cut;
    }

    // Base do array de codigos da dupla (jogador, dispositivo) — mesma varredura do motor
    // sobre a tabela 0x005793D0 (entradas de 16 bytes, campo [0]=0 termina a lista).
    static long CtrlEntryArr(int player, int dev)
    {
        long t = ResolveAddr(IV_CTRL_ENTRY_TBL);
        for (int i = 0; i < 8; i++)
        {
            long e = t + i * 16;
            if (ReadU32(hProc, e) == 0) return 0;
            if ((int)ReadU32(hProc, e + 8) == player && (int)ReadU32(hProc, e + 12) == dev)
                return (long)ReadU32(hProc, e + 4);
        }
        return 0;
    }

    // Tipo do dispositivo do jogador (0=teclado, 1=gamepad DirectInput, 0x168=XInput)
    static int CtrlDeviceType(int pane)
    {
        int d = (int)ReadU32(hProc, ResolveAddr(IV_CTRL_DEV_TYPE) + pane * 4);
        return (d == 0x168 || d == 1) ? d : 0;
    }

    static string CtrlKeyText(int code)
    {
        switch (code)
        {
            case 200: return L4("Up arrow", "Flecha arriba", "上矢印", "seta para cima");
            case 208: return L4("Down arrow", "Flecha abajo", "下矢印", "seta para baixo");
            case 203: return L4("Left arrow", "Flecha izquierda", "左矢印", "seta para esquerda");
            case 205: return L4("Right arrow", "Flecha derecha", "右矢印", "seta para direita");
            case 57:  return L4("Space", "Espacio", "スペース", "Espaço");
            case 28:  return L4("Enter", "Intro", "エンター", "Enter");
        }
        string t = CtrlText("key_" + code);
        if (t.Length > 0) return t;
        return CtrlFmt("key_unknown", code, L4("Keycode", "Clave", "キーコード", "código de tecla"));
    }

    static string CtrlButtonText(int code)
    {
        if (code == -1)
        {
            string n = CtrlText("key_-1");
            return n.Length > 0 ? n : "--------";
        }
        string t = CtrlText("button_360_" + code);
        if (t.Length > 0) return t;
        return CtrlFmt("button_generic", code, L4("Button", "Botón", "ボタン", "Botão"));
    }

    // Quadro de renderizacao do motor (FUN_0045e670 @0x45e94b..0x45e9a7)
    static string CtrlCodeText(int dev, int code)
    {
        if (dev == 0) return CtrlKeyText(code);                 // teclado -> "key_%d"
        if (dev == 0x168) return CtrlButtonText(code);          // XInput  -> "button_360_%d"
        if (dev == 1)                                           // gamepad DirectInput -> switch
        {
            if (code == -1 || (code >= 0 && code <= 3) || (code >= 0x10 && code <= 0x13))
                return CtrlButtonText(code);
            return CtrlFmt("button_generic", code, L4("Button", "Botón", "ボタン", "Botão"));
        }
        return "";
    }

    // Linha "Dispositivo" (FUN_0045e670 @0x45e745..0x45e8a7)
    static string CtrlDeviceRowText(int pane)
    {
        int dev = CtrlDeviceType(pane);
        int handle = (int)ReadU32(hProc, ResolveAddr(IV_CTRL_HANDLE) + pane * 4);
        if (dev == 0)
        {
            string n = CtrlText("none");
            return n.Length > 0 ? n : L4("None", "Ninguna", "なし", "Nenhum");
        }
        if (dev == 0x168)
            return CtrlFmt("xinput", handle, L4("XInput controller", "Mando XInput", "XInput", "Controle XInput"));
        if (handle >= 1 && handle <= 8)
        {
            string nm = ReadCString(hProc, ResolveAddr(IV_CTRL_DEVNAME) + (long)handle * IV_CTRL_DEVNAME_STRIDE, 48);
            if (nm != null && nm.Length > 0 && IsPrintableKey(nm)) return nm;
        }
        string j = CtrlText("joystick");
        return j.Length > 0 ? j : L4("Controller", "Control", "ゲームパッド", "Controle");
    }

    // Valor de uma linha. Devolve o valor do MAPEAMENTO EM USO (a tecla/botao realmente
    // atribuido = o array que o motor poe no socket do jogador) e, em screenValue, o que a TELA
    // desenha; os dois so diferem quando o dispositivo ativo e o teclado (ver consts).
    static string CtrlRowValue(int menuId, int pane, int row, out string screenValue, out int rawCode)
    {
        screenValue = ""; rawCode = int.MinValue;
        int rowTotal = CtrlRowTotal(menuId);
        int labelOff = (menuId == 0x2e) ? 1 : 0;
        if (row < 0 || row >= rowTotal) return "";
        if (row == 0 && labelOff == 0) return CtrlDeviceRowText(pane);
        int record = row + labelOff - 1;
        if (record < 0 || record > 15) return "";
        int dev = CtrlDeviceType(pane);
        long arr = CtrlEntryArr(pane + 1, dev);
        if (arr == 0) return "";
        int code = (int)ReadU32(hProc, arr + (long)record * 8 + 4);
        rawCode = code;
        if (dev == 0)
        {
            long sa = CtrlEntryArr(pane + 1, 0x168);
            if (sa != 0) screenValue = CtrlCodeText(0x168, (int)ReadU32(hProc, sa + (long)record * 8 + 4));
        }
        else screenValue = CtrlCodeText(dev, code);
        return CtrlCodeText(dev, code);
    }

    // Rotulo da linha (tabela estatica 0x00577C50, com o deslocamento da variante 0x2E)
    static string CtrlRowLabelText(int menuId, int row)
    {
        int labelOff = (menuId == 0x2e) ? 1 : 0;
        long tbl = ResolveAddr(IV_CTRL_KEYTABLE);
        uint kp = ReadU32(hProc, tbl + (long)(row + labelOff) * 4);
        if (kp < 0x00010000 || kp > 0x7fff0000) return "";
        string k = ReadCString(hProc, kp, 48);
        return IsPrintableKey(k) ? ControlRowLabel(k) : "";
    }

    // Qual painel (0 = Jogador 1, 1 = Jogador 2) esta sendo editado
    static int CtrlPane(uint menu, string selNode)
    {
        bool isRight = selNode != null && selNode.StartsWith("right");
        if (!isRight && (selNode == null || !selNode.StartsWith("left")))
        {
            byte flagL = ReadU8(hProc, menu + IV_OFF_CTRL_PANE);
            byte flagR = ReadU8(hProc, menu + IV_OFF_CTRL_PANE + 1);
            isRight = (flagR != 0 && flagL == 0);
        }
        return isRight ? 1 : 0;
    }

    // F7: recita o mapeamento inteiro da tela de Controles (Jogador 1 e Jogador 2)
    static void ReciteControls()
    {
        if (hProc == IntPtr.Zero) { Speak(L4("Game not connected.", "Juego no conectado.", "未接続", "Jogo não conectado.")); return; }
        if (mgr == 0) mgr = ScanForManager(hProc);
        if (mgr == 0) { Speak(L4("Game not connected.", "Juego no conectado.", "未接続", "Jogo não conectado.")); return; }
        uint menu = ReadU32(hProc, mgr + OFF_MGR_MENU);
        uint vt = (menu >= 0x10000) ? ReadU32(hProc, menu) : 0;
        // Funciona em QUALQUER tela: a tabela de rotulos e os DADOS do mapeamento sao globais.
        // Com a tela de Controles aberta usa-se o id dela (geometria real); fora dela usa-se 6
        // (a variante que o IV usa — provado em FUN_00461660 -> FUN_00467e80(6)).
        int menuId = (vt == IVVT_ControlSettingsMenu && menu >= 0x10000) ? CtrlMenuId(menu) : 6;

        int rowTotal = CtrlRowTotal(menuId);
        string full = "";
        for (int pane = 0; pane < 2; pane++)
        {
            if (pane == 1)
            {
                int h0 = (int)ReadU32(hProc, ResolveAddr(IV_CTRL_HANDLE));
                int h1 = (int)ReadU32(hProc, ResolveAddr(IV_CTRL_HANDLE) + 4);
                if (h1 == 0 || h1 == h0) break;     // so existe um painel
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(MenuName(IVVT_ControlSettingsMenu));
            sb.Append(", ");
            sb.Append(pane == 0 ? L4("Player 1", "Jugador 1", "プレイヤー1", "Jogador 1")
                                : L4("Player 2", "Jugador 2", "プレイヤー2", "Jogador 2"));
            sb.Append(": ");
            int n = 0;
            for (int r = 0; r < rowTotal; r++)
            {
                string lab = CtrlRowLabelText(menuId, r);
                if (lab.Length == 0) continue;
                string sv; int rc;
                string val = CtrlRowValue(menuId, pane, r, out sv, out rc);
                if (n > 0) sb.Append(", ");
                sb.Append(lab);
                if (val.Length > 0) sb.Append(": ").Append(val);
                n++;
            }
            if (n == 0) continue;
            sb.Append(", ").Append(rowTotal).Append(" ")
              .Append(L4("rows", "filas", "行", "linhas"));
            full = (full.Length == 0) ? sb.ToString() : (full + ". " + sb.ToString());
            if (verbose) Console.WriteLine("[dbg] controls-recite painel=" + pane + " linhas=" + rowTotal);
        }
        if (full.Length == 0) full = MenuName(vt);
        Log("[recital controles] " + full);
        Speak(full);
    }

    static bool PollMenu(IntPtr hProc, uint menu, uint vt, bool force)
    {
        string name = MenuName(vt);

        string ofWord = (currentLang == "en") ? " of " : (currentLang == "es") ? " de " : (currentLang == "ja") ? " / " : " de ";
        string p1Word = (currentLang == "en") ? "Player 1" : (currentLang == "es") ? "Jugador 1" : (currentLang == "ja") ? "プレイヤー1" : "Jogador 1";
        string p2Word = (currentLang == "en") ? "Player 2" : (currentLang == "es") ? "Jugador 2" : (currentLang == "ja") ? "プレイヤー2" : "Jogador 2";
        string optWord = (currentLang == "en") ? "Option " : (currentLang == "es") ? "Opción " : (currentLang == "ja") ? "設定 " : "Opção ";
        string moveWord = (currentLang == "en") ? "Move " : (currentLang == "es") ? "Movimiento " : (currentLang == "ja") ? "技 " : "Golpe ";
        string charWord = (currentLang == "en") ? "Character " : (currentLang == "es") ? "Personaje " : (currentLang == "ja") ? "キャラクター " : "Personagem ";
        string illustWord = (currentLang == "en") ? "Illustration " : (currentLang == "es") ? "Ilustración " : (currentLang == "ja") ? "イラスト " : "Ilustração ";
        string trophyWord = (currentLang == "en") ? "Trophy " : (currentLang == "es") ? "Trofeo " : (currentLang == "ja") ? "トロフィー " : "Troféu ";
        string boardWord = (currentLang == "en") ? "Leaderboard " : (currentLang == "es") ? "Marcador " : (currentLang == "ja") ? "ランキング " : "Placar ";

        // 1. GalleryMenu (0x4b73a4): 29 illustrations indexed in 0x5f02cc
        if (vt == 0x4b73a4)
        {
            uint gIdx = ReadU32(hProc, 0x5f02cc);
            if (gIdx >= 29) gIdx = 0;
            int g_idx = (int)gIdx;
            int g_count = 29;
            string g_disp = illustWord + (g_idx + 1);
            string g_msg = name + ", " + g_disp + ", " + (g_idx + 1) + ofWord + g_count;

            string g_dedup = vt.ToString("x8") + "|" + g_idx;
            if (!force && g_dedup == lastDedup) return false;
            lastDedup = g_dedup;
            if (verbose) Console.WriteLine("[dbg] gallery idx=" + g_idx);
            Speak(g_msg);
            return true;
        }

        // 2. TrophiesMenu (0x4b8e24): 18 trophies indexed at menu + 0x1e8 with dynamic requirements
        if (vt == 0x4b8e24)
        {
            uint tIdx = ReadU32(hProc, menu + 0x1e8);
            if (tIdx >= 18) tIdx = 0;
            int t_idx = (int)tIdx;
            int t_count = 18;

            string titleKey = "Trophy" + (t_idx + 1) + "Title";
            string bodyKey = "Trophy" + (t_idx + 1) + "Body";
            string t_title = (stringMap.ContainsKey(titleKey) && stringMap[titleKey].Length > 0) ? stringMap[titleKey] : (t_idx < TrophyNames.Length ? TrophyNames[t_idx] : (trophyWord + (t_idx + 1)));
            string t_desc = (stringMap.ContainsKey(bodyKey) && stringMap[bodyKey].Length > 0) ? stringMap[bodyKey] : "";

            string t_msg = name + ", " + t_title;
            if (t_desc.Length > 0) t_msg += ": " + t_desc;
            t_msg += ", " + (t_idx + 1) + ofWord + t_count;

            string t_dedup = vt.ToString("x8") + "|" + t_idx;
            if (!force && t_dedup == lastDedup) return false;
            lastDedup = t_dedup;
            if (verbose) Console.WriteLine("[dbg] trophies idx=" + t_idx + " title=" + t_title + " desc=" + t_desc);
            Speak(t_msg);
            return true;
        }

        // 3. LeaderboardsMenu (0x4b79cc) - mode selector
        if (vt == 0x4b79cc)
        {
            uint lIdx = ReadU32(hProc, menu + 0x160);
            uint lCnt = ReadU32(hProc, menu + 0x16c);
            if (lCnt < 1 || lCnt > 64) lCnt = (uint)LeaderboardModes.Length;
            if (lIdx >= lCnt) lIdx = 0;
            int l_idx = (int)lIdx;
            int l_count = (int)lCnt;
            string l_disp = GetLeaderboardModeName(l_idx);
            if (l_disp == null) l_disp = boardWord + (l_idx + 1);

            string l_msg = name + ", " + l_disp + ", " + (l_idx + 1) + ofWord + l_count;

            string l_dedup = vt.ToString("x8") + "|" + l_idx;
            if (!force && l_dedup == lastDedup) return false;
            lastDedup = l_dedup;
            if (verbose) Console.WriteLine("[dbg] leaderboards idx=" + l_idx);
            Speak(l_msg);
            return true;
        }

        // 3b. RankingsMenu (0x4b8684) - inside the ranking leaderboard table
        if (vt == 0x4b8684)
        {
            string r_rankWord = (currentLang == "en") ? "Rank " : (currentLang == "es") ? "Puesto " : (currentLang == "ja") ? "順位 " : "Posição ";
            string r_scoreWord = (currentLang == "en") ? "Score: " : (currentLang == "es") ? "Puntos: " : (currentLang == "ja") ? "スコア: " : "Pontos: ";

            uint selNamePtr = ReadU32(hProc, menu + OFF_MENU_SELNAME);
            string selNode = (selNamePtr >= 0x10000) ? ReadCString(hProc, selNamePtr, 32) : "";

            int lineIdx = -1;
            if (selNode.StartsWith("line"))
            {
                int ln;
                if (int.TryParse(selNode.Substring(4), out ln)) lineIdx = ln - 1;
            }

            int scroll = (int)ReadU32(hProc, menu + 0x104);
            int targetIdx = (lineIdx >= 0) ? (scroll + lineIdx) : scroll;
            int selRank = (int)ReadU32(hProc, menu + 0x140);

            uint head = ReadU32(hProc, menu + 0x74);
            uint targetEntry = 0;
            if (head >= 0x10000 && head <= 0x7fff0000)
            {
                uint curr = ReadU32(hProc, head);
                int entryIndex = 0;
                while (curr >= 0x10000 && curr <= 0x7fff0000 && curr != head && entryIndex < 100)
                {
                    uint r = ReadU32(hProc, curr + 0x94);
                    if (selRank > 0 && r == (uint)selRank)
                    {
                        targetEntry = curr;
                        break;
                    }
                    if (entryIndex == targetIdx)
                    {
                        targetEntry = curr;
                    }
                    curr = ReadU32(hProc, curr);
                    entryIndex++;
                }
            }

            if (targetEntry == 0) targetEntry = ReadU32(hProc, menu + 0xec);

            if (targetEntry >= 0x10000 && targetEntry <= 0x7fff0000)
            {
                uint rank = ReadU32(hProc, targetEntry + 0x94);
                uint score = ReadU32(hProc, targetEntry + 0x98);
                string playerName = ReadCString(hProc, targetEntry + 0x0c, 64);
                if (playerName.Length == 0) playerName = ReadCString(hProc, ReadU32(hProc, targetEntry + 8), 64);

                string r_msg = name + ", " + r_rankWord + rank + ": " + playerName + ", " + r_scoreWord + score;
                string r_dedup = vt.ToString("x8") + "|" + rank + "|" + playerName + "|" + score + "|" + selNode;
                if (!force && r_dedup == lastDedup) return false;
                lastDedup = r_dedup;
                if (verbose) Console.WriteLine("[dbg] rankings entry=" + rank + " player=" + playerName + " score=" + score);
                Speak(r_msg);
                return true;
            }
            else
            {
                int rIdx = (lineIdx >= 0) ? lineIdx : 0;
                uint rCount = ReadU32(hProc, menu + 0x74);
                if (rCount < 1 || rCount > 100) rCount = 10;
                string r_msg = name + ", " + r_rankWord + (rIdx + 1) + ofWord + rCount;
                string r_dedup = vt.ToString("x8") + "|" + rIdx + "|" + selNode;
                if (!force && r_dedup == lastDedup) return false;
                lastDedup = r_dedup;
                Speak(r_msg);
                return true;
            }
        }

        // 3c. LobbyMenu (0x4b7c54) and RoomMenu (0x4b8a8c)
        if (vt == 0x4b7c54 || vt == 0x4b8a8c)
        {
            uint ctrl = ReadU32(hProc, menu + 0x1c);
            int roomIdx = -1;
            int roomCount = 6;
            if (ctrl >= 0x10000 && ctrl <= 0x7fff0000)
            {
                roomIdx = (int)ReadU32(hProc, ctrl + 0x160);
                uint rc = ReadU32(hProc, ctrl + 0x164);
                if (rc >= 1 && rc <= 10) roomCount = (int)rc;
            }

            uint selNamePtr = ReadU32(hProc, menu + OFF_MENU_SELNAME);
            string selNode = (selNamePtr >= 0x10000) ? ReadCString(hProc, selNamePtr, 64) : "";

            byte isReady = ReadU8(hProc, menu + 0x8b);
            byte hostReady = ReadU8(hProc, menu + 0x8c);

            string itemLabel = "";
            string itemVal = "";

            if (roomIdx >= 0)
            {
                switch (roomIdx)
                {
                    case 0:
                        itemLabel = (currentLang == "pt") ? "Anfitrião" : (currentLang == "es") ? "Anfitrión" : (currentLang == "ja") ? "ホスト" : "Host";
                        break;
                    case 1:
                        itemLabel = (currentLang == "pt") ? "Atraso de Entrada" : (currentLang == "es") ? "Retardo de Entrada" : (currentLang == "ja") ? "入力遅延" : "Input Delay";
                        break;
                    case 2:
                        itemLabel = (currentLang == "pt") ? "Pronto?" : (currentLang == "es") ? "¿Listo?" : (currentLang == "ja") ? "準備完了?" : "Ready?";
                        itemVal = (isReady != 0) ? ((currentLang == "pt") ? "Sim" : (currentLang == "es") ? "Sí" : (currentLang == "ja") ? "はい" : "Yes") :
                                                   ((currentLang == "pt") ? "Não" : (currentLang == "es") ? "No" : (currentLang == "ja") ? "いいえ" : "No");
                        break;
                    case 3:
                        itemLabel = (currentLang == "pt") ? "Atraso Mínimo de Entrada" : (currentLang == "es") ? "Retardo Mínimo" : (currentLang == "ja") ? "最小入力遅延" : "Minimum Input Delay";
                        break;
                    case 4:
                        itemLabel = (currentLang == "pt") ? "Convidar" : (currentLang == "es") ? "Invitar" : (currentLang == "ja") ? "招待" : "Invite";
                        break;
                    case 5:
                        itemLabel = (currentLang == "pt") ? "Voltar" : (currentLang == "es") ? "Volver" : (currentLang == "ja") ? "戻る" : "Back";
                        break;
                    default:
                        itemLabel = optWord + (roomIdx + 1);
                        break;
                }
            }
            else
            {
                if (selNode.Contains("host")) itemLabel = (currentLang == "pt") ? "Anfitrião" : "Host";
                else if (selNode.Contains("delay_setting") || selNode.Contains("min_delay")) itemLabel = (currentLang == "pt") ? "Atraso Mínimo de Entrada" : "Minimum Input Delay";
                else if (selNode.Contains("delay")) itemLabel = (currentLang == "pt") ? "Atraso de Entrada" : "Input Delay";
                else if (selNode.Contains("ready")) {
                    itemLabel = (currentLang == "pt") ? "Pronto?" : "Ready?";
                    itemVal = (isReady != 0) ? ((currentLang == "pt") ? "Sim" : "Yes") : ((currentLang == "pt") ? "Não" : "No");
                }
                else if (selNode.Contains("invite")) itemLabel = (currentLang == "pt") ? "Convidar" : "Invite";
                else if (selNode.Contains("back")) itemLabel = (currentLang == "pt") ? "Voltar" : "Back";
                else itemLabel = selNode.Length > 0 ? selNode : name;
            }

            string r_msg = name + ", " + itemLabel;
            if (itemVal.Length > 0) r_msg += ": " + itemVal;
            if (roomIdx >= 0) r_msg += ", " + (roomIdx + 1) + ofWord + roomCount;

            string r_dedup = vt.ToString("x8") + "|" + roomIdx + "|" + isReady + "|" + hostReady + "|" + selNode;
            if (!force && r_dedup == lastDedup) return false;
            lastDedup = r_dedup;
            if (verbose) Console.WriteLine("[dbg] room vt=" + vt.ToString("x") + " idx=" + roomIdx + " node=" + selNode);
            Speak(r_msg);
            return true;
        }

        // 4. SettingsMenu family (GameSettings, DisplaySettings, VolumeSettings, SystemSettings, HostSettings, Settings)
        bool ivSettings = (vt == IVVT_GameSettingsMenu || vt == IVVT_DisplaySettingsMenu ||
                           vt == IVVT_VolumeSettingsMenu || vt == IVVT_SystemSettingsMenu ||
                           vt == IVVT_SettingsMenu || vt == IVVT_HostSettingsMenu);
        if (vt == 0x4b8c64 || vt == 0x4b7614 || vt == 0x4b729c || vt == 0x4b919c || vt == 0x4b8d8c || vt == 0x4b786c || ivSettings)
        {
            // IV: every field of this family sits 8 bytes further out than in the V Special
            // (measured live: +0x74 rows, +0x78 keys, +0x84 idx, +0x88 count).
            long oIdx = ivSettings ? IV_OFF_SET_IDX : 0x7c;
            long oCnt = ivSettings ? IV_OFF_SET_COUNT : 0x80;
            long oCnt2 = ivSettings ? IV_OFF_SET_COUNT2 : 0x9c;
            long oKeysArr = ivSettings ? IV_OFF_SET_KEYS : 0x70;
            long oItemsArr = ivSettings ? IV_OFF_SET_ITEMS : 0x6c;
            long oModRow = ivSettings ? 0x8c : 0x84;
            long oModVal = ivSettings ? 0x94 : 0x8c;

            int s_idx = (int)ReadU32(hProc, menu + oIdx);
            int s_count = (int)ReadU32(hProc, menu + oCnt);
            if (s_count <= 0 || s_count > 64) s_count = (int)ReadU32(hProc, menu + oCnt2);
            if (s_count <= 0 || s_count > 64) s_count = 8;
            if (s_idx < 0) s_idx = 0;
            if (s_idx >= s_count) s_idx = s_count - 1;

            // The engine's own node name ("lineN", 1-based) is the authoritative cursor; the
            // numeric field is only a fallback (measured live: +0x84 tracks it, but the node
            // name is what FUN_0046b930/00476310 build, so prefer it).
            int s_idxByName = ParseNodeIndex(ReadCString(hProc, ReadU32(hProc, menu + OFF_MENU_SELNAME), 32));
            if (s_idxByName >= 0) s_idx = s_idxByName;

            uint keysArr = ReadU32(hProc, menu + oKeysArr);
            uint itemsArr = ReadU32(hProc, menu + oItemsArr);

            // Contador falado ("N de M"). Nas linhas comuns e a LINHA dentro da tela; na linha de
            // IDIOMA passa a ser a POSICAO DO IDIOMA dentro das escolhas (4 idiomas), porque e
            // isso que o jogador precisa saber ao trocar de idioma. Sem isto ele ouvia "1 de 1".
            int cntCur = s_idx + 1;
            int cntMax = s_count;
            string ivProof = null;
            string ivLangList = null;

            string s_key = "";
            if (keysArr >= 0x10000)
            {
                uint kp = ReadU32(hProc, (long)keysArr + (long)s_idx * 4);
                if (kp >= 0x10000) s_key = ReadCString(hProc, kp, 64);
            }
            string keyLabel = (s_key.Length > 0 && stringMap.ContainsKey(s_key)) ? stringMap[s_key] : s_key;
            if (keyLabel.Length == 0) keyLabel = optWord + (s_idx + 1);

            int curVal = 0;
            string valDisp = "";
            string ivDbg = null;
            if (itemsArr >= 0x10000)
            {
                long itemPtr = (long)itemsArr + (long)s_idx * 20;
                uint choiceKeys = ReadU32(hProc, itemPtr + 0);
                int numChoices = (int)ReadU32(hProc, itemPtr + 4);
                byte rowType = ReadU8(hProc, itemPtr + 8);

                if (ivSettings)
                {
                    // ===== IV =====
                    // row+8 IS the Options.csv "Setting ID" (measured on every row of the Game,
                    // Display, Volume and System screens) and the engine's own getter answers for
                    // it: FUN_00482ab0(id) over 0x631B44 -> 0x631B40 -> 0x631B24. The value the
                    // screen highlights comes from that very getter (vtable+0x80 -> FUN_004733d0),
                    // so speaking it can never disagree with the game. -1 => speak the label only.
                    int ivVal = (rowType >= 0x20 && rowType < 0x7f) ? IvOptionValue(hProc, (char)rowType) : -1;
                    // Chave da escolha mostrada E chave crua: o texto falado e SEMPRE o texto que o
                    // motor calcula para a escolha DAQUELA linha (FUN_004760c0). E isto que impede a
                    // inversao: 'level_disp' tem (on,off) e 'filter'/'vsync'/'flicker' tem (off,on),
                    // e a linha 'fullscreen' tem (no,yes) - com "Ligado/Desligado" fixo a linha
                    // ficaria invertida. Aqui o texto vem da propria escolha, nunca de um padrao.
                    string ivChoiceKey = IvChoiceKey(hProc, choiceKeys, ivVal);
                    bool ivLangRow = IsLanguageKey(ivChoiceKey) || s_key == "option_language";
                    if (verbose)
                        Console.WriteLine("[dbg] ivopt id='" + (char)rowType + "' val=" + ivVal +
                                          " row=" + s_idx + " key=" + s_key + " chain: " + IvOptionChainDump(hProc));
                    if (ivVal >= 0)
                    {
                        curVal = ivVal;
                        if (ivLangRow)
                        {
                            // Linha de IDIOMA: o nome vem do codigo do motor ([0x631B28]) e o
                            // contador passa a ser a posicao do idioma (ex.: 3 de 4). A lista
                            // completa dos idiomas e anexada em toda fala desta linha (entrada e
                            // navegacao), sempre traduzida no idioma do narrador.
                            string langName = IvLanguageName(hProc, ivVal);
                            if (langName.Length > 0) valDisp = langName;
                            else if (choiceKeys >= 0x10000 && numChoices >= 1 && ivVal < numChoices)
                                valDisp = IvChoiceText(hProc, choiceKeys, ivVal);
                            if (numChoices >= 2) { cntMax = numChoices; cntCur = ivVal + 1; }
                            ivLangList = IvLanguageList(hProc, menu, s_idx);
                            if (ivLangList.Length == 0) ivLangList = null;
                        }
                        else if (choiceKeys >= 0x10000 && numChoices >= 1 && ivVal < numChoices)
                        {
                            valDisp = IvChoiceText(hProc, choiceKeys, ivVal);
                        }
                        else if (choiceKeys < 0x10000 && numChoices >= 2 && ivVal <= 10)
                        {
                            // Volume rows (game_sfx/menu_sfx/menu_music): choiceKeys is NULL and
                            // numChoices is the level count; the engine applies value/10.0
                            // (DAT_004e7a6c = 10.0), so index 4 = 40%. Acima de 10 nao e um nivel
                            // valido => nao fala nada (regra: nunca falar valor errado).
                            valDisp = (ivVal * 10) + "%";
                        }
                    }
                    ivDbg = "[dbg] ivopt id='" + (char)rowType + "' val=" + ivVal +
                            " row=" + s_idx + " key=" + s_key + " valDisp='" + valDisp + "' chain: " + IvOptionChainDump(hProc);
                    // Prova da conferencia: id -> valor -> chave da escolha daquela linha -> texto
                    // falado. Uma linha por fala das telas de configuracao, para a tabela dos
                    // booleanos: toda linha com chave option_on/option_off/option_yes/option_no.
                    if (rowType >= 0x20 && rowType < 0x7f)
                    {
                        string codes = IvLanguageCodes(hProc);
                        ivProof = "[dbg] ivrow id='" + (char)rowType + "' val=" + ivVal +
                                  " key=" + s_key + " escolha='" + ivChoiceKey + "' texto='" + valDisp + "'" +
                                  ((codes.Length > 0) ? (" idiomas=[" + codes + "] idiomaAtual=" + IvOptionValue(hProc, 'L')) : "");
                    }
                    // A linha de IDIOMA nao tem mais caminho especial: o texto dela (nome do idioma
                    // + posicao "2 de 4") e a lista completa sao montados no caminho geral, abaixo.
                    // Assim a leitura acontece ao ENTRAR na tela e em CADA navegacao da linha.
                }
                else
                {
                // Retrieve real live option value from the engine's option buffer
                int engVal = GetEngineOptionValue(hProc, rowType);
                if (engVal >= 0)
                {
                    curVal = engVal;
                }
                else
                {
                    curVal = (int)ReadU32(hProc, itemPtr + 12);
                }
                uint choiceDisplays = ReadU32(hProc, itemPtr + 16);

                // LIVE VALUE OVERRIDE:
                // When Left/Right is pressed, SSVS writes the active modified row index to menu + 0x84
                // and the newly selected value to menu + 0x8c!
                uint lastModRow = ReadU32(hProc, menu + oModRow);
                if (lastModRow == (uint)s_idx)
                {
                    curVal = (int)ReadU32(hProc, menu + oModVal);
                }

                if (numChoices == 11 || s_key.Contains("volume") || s_key.Contains("sfx") || s_key.Contains("music") || s_key.Contains("scanlines"))
                {
                    valDisp = (curVal * 10) + "%";
                }
                else if (rowType == (byte)'N')
                {
                    valDisp = curVal.ToString();
                }
                else if (choiceKeys >= 0x10000 && numChoices > 0 && curVal >= 0 && curVal < numChoices)
                {
                    uint ck = ReadU32(hProc, (long)choiceKeys + (long)curVal * 4);
                    if (ck >= 0x10000)
                    {
                        string cks = ReadCString(hProc, ck, 64);
                        if (cks.StartsWith("option_volume_"))
                        {
                            int v;
                            if (int.TryParse(cks.Substring(14), out v)) valDisp = (v * 10) + "%";
                            else valDisp = (curVal * 10) + "%";
                        }
                        else if (cks == "option_language_0") valDisp = "English";
                        else if (cks == "option_language_1") valDisp = "Español";
                        else if (cks == "option_language_2") valDisp = "Português";
                        else if (cks == "option_language_3") valDisp = "日本語";
                        else if (cks == "option_netcode_best") valDisp = (currentLang == "pt" ? "Melhor" : currentLang == "es" ? "Mejor" : currentLang == "ja" ? "最適" : "Best");
                        else if (cks == "option_netcode_fixed") valDisp = (currentLang == "pt" ? "Fixo" : currentLang == "es" ? "Fijo" : currentLang == "ja" ? "固定" : "Fixed");
                        // Texto REAL da escolha = o mesmo que a tela mostra (tabela do motor lida
                        // pelo narrador): 'option_on'->"On", 'option_off'->"Off", 'option_yes'->
                        // "Sim", 'option_no'->"Nao". O rotulo fixo "Ligado/Desligado" so entra se a
                        // tabela nao tiver a chave - assim o texto falado nunca contradiz a tela.
                        else if ((cks == "option_yes" || cks == "option_on" || cks == "option_no" || cks == "option_off") && stringMap.ContainsKey(cks)) valDisp = stringMap[cks];
                        else if (cks == "option_yes" || cks == "option_on") valDisp = (currentLang == "pt" ? "Ligado" : currentLang == "es" ? "Activado" : currentLang == "ja" ? "オン" : "On");
                        else if (cks == "option_no" || cks == "option_off") valDisp = (currentLang == "pt" ? "Desligado" : currentLang == "es" ? "Desactivado" : currentLang == "ja" ? "オフ" : "Off");
                        else if (stringMap.ContainsKey(cks)) valDisp = stringMap[cks];
                        else valDisp = cks;
                    }
                }
                else if (choiceDisplays >= 0x10000 && numChoices > 0 && curVal >= 0 && curVal < numChoices)
                {
                    uint dp = ReadU32(hProc, (long)choiceDisplays + (long)curVal * 4);
                    if (dp >= 0x10000) valDisp = ReadCString(hProc, dp, 64);
                }

                if (valDisp.Length == 0 && (s_key.Contains("smoothing") || s_key.Contains("suavizacao") || s_key.Contains("fullscreen") || s_key.Contains("tela_inteira")))
                {
                    valDisp = (curVal != 0) ? ((currentLang == "pt") ? "Ligado" : "On") : ((currentLang == "pt") ? "Desligado" : "Off");
                }
                else if (valDisp.Length == 0 && s_key == "option_defaults")
                {
                    // Action button, no value needed
                }
                else if (valDisp.Length == 0 && numChoices > 0)
                {
                    valDisp = curVal.ToString();
                }
                }
            }

            string s_msg;
            string s_lab = keyLabel.TrimEnd();
            string s_sep = s_lab.EndsWith(":") ? " " : ": ";
            if (valDisp.Length > 0)
                s_msg = name + ", " + s_lab + s_sep + valDisp + ", " + cntCur + ofWord + cntMax;
            else
                s_msg = name + ", " + s_lab + ", " + cntCur + ofWord + cntMax;
            // Linha de IDIOMA: alem do idioma atual, sempre a LISTA completa (traduzida no idioma
            // do narrador), para o jogador saber entre quais idiomas esta navegando.
            if (ivLangList != null && ivLangList.Length > 0)
                s_msg += ". " + LanguageChoicesWord() + ": " + ivLangList;

            string s_dedup = vt.ToString("x8") + "|" + s_idx + "|" + curVal + "|" + valDisp + "|" + s_key;
            if (!force && s_dedup == lastDedup) return false;
            lastDedup = s_dedup;
            if (verbose)
            {
                Console.WriteLine("[dbg] settings vt=" + vt.ToString("x") + " idx=" + s_idx + " key=" + s_key + " val=" + valDisp);
                if (ivDbg != null) Log(ivDbg);      // prova dentro do log.txt
                if (ivProof != null) Log(ivProof);  // prova da conferencia dos booleanos
            }
            Speak(s_msg);
            return true;
        }

        // 4b. PopupMenu (IV 0x0053C72C) - the "SIM / NAO" confirmation box.
        // FUN_0046d510 (ctor) stores the choice-key array at +0x80 and the choice COUNT at +0x84;
        // the selected choice is the node name at +0x28.
        // TWO measured defects on the pause "SAIR DO JOGO" box (both proven by disassembly in the
        // 2026-10-28 session; the old code hit both):
        //  (a) +0x80 is NOT a durable key array. Its only creator, FUN_00467850, calls the ctor as
        //      FUN_0046d510(new, parent, titleKey, &local_24, count, defaultKey) where local_24 is a
        //      STACK array of 4 char*: +0x80 therefore points into FUN_00467850's frame and dangles
        //      as soon as it returns. Live proof: "[dbg] popup mode=2 labels=0x19fe80 count=0"
        //      (0x19fe80 is a stack address, so the two keys never resolved). The array is used only
        //      while it still resolves; otherwise the labels come from the popup's OPENER, which the
        //      engine keeps at MenuManager+0x24 (FUN_00467ca0: mgr[0x24] = old mgr[0x1c]).
        //  (b) the choice nodes are "choice0"/"choice1" - 0-BASED. The ctor writes the first label
        //      by literal 0x0053C02C="choice0" and FUN_0046da30 builds "choice%d" from i=0, so the
        //      1-based ParseNodeIndex returned -1/0 for them and the spoken index was always 0.
        if (vt == IVVT_PopupMenu)
        {
            uint p_labels = ReadU32(hProc, menu + IV_OFF_POPUP_LABELS);
            int  p_count  = (int)ReadU32(hProc, menu + IV_OFF_POPUP_MODE);
            if (p_count < 1 || p_count > 8) p_count = 1;

            // Selected choice: node name at +0x28 is "choice%d" with a 0-BASED d.
            string p_node = ReadCString(hProc, ReadU32(hProc, menu + OFF_MENU_SELNAME), 32);
            int p_idx = ChoiceNodeIndex(p_node);
            if (p_idx < 0) p_idx = 0;
            if (p_idx >= p_count) p_idx = p_count - 1;

            // Labels, source 1: the key array - trusted only while every entry still resolves to a
            // key the engine itself knows (IsPrintableKey + present in Data\strings.txt).
            string[] p_keys = new string[p_count];
            int p_arrN = 0;
            if (p_labels >= 0x00010000 && p_labels <= 0x7fff0000)
            {
                while (p_arrN < p_count)
                {
                    uint kp = ReadU32(hProc, (long)p_labels + (long)p_arrN * 4);
                    if (kp < 0x00010000 || kp > 0x7fff0000) break;
                    string ks = ReadCString(hProc, kp, 48);
                    if (!IsPrintableKey(ks) || !stringMap.ContainsKey(ks)) break;
                    p_keys[p_arrN] = ks;
                    p_arrN++;
                }
            }

            // Labels, source 2: the OPENER. The engine records the menu that pushed the current
            // popup at MenuManager+0x24 (FUN_00467ca0: mgr[0x24] = mgr[0x1c] right before
            // mgr[0x1c] = new popup). The live run confirms the pause popup really is the active
            // menu, so mgr[0x24] is the PauseMenu while it is open. Both confirmations the
            // PauseMenu opens (FUN_0046c6d0 @0x46c9b0 / @0x46cb46) and the MainMenu quit box
            // (FUN_00467850("confirm_quit_main",...)) use exactly the keys
            // "option_yes_text" / "option_no_text":
            //   FUN_00467850("confirm_quit_destructive","option_yes_text","*option_no_text",0,0,0)
            //   FUN_00467850("confirm_reset",            "option_yes_text","*option_no_text",0,0,0)
            //   FUN_00467850("confirm_quit_main",        "option_yes_text","*option_no_text",0,0,0)
            // Popups whose opener is not in this table fall through to "no label" (never junk).
            uint p_mgr = ReadU32(hProc, menu + 0x1c);
            if (p_mgr < 0x00010000 || p_mgr > 0x7fff0000) p_mgr = (uint)mgr;
            uint p_opener = ReadU32(hProc, (long)p_mgr + OFF_MGR_OPENER);
            uint p_openerVt = (p_opener >= 0x00010000 && p_opener <= 0x7fff0000) ? ReadU32(hProc, p_opener) : 0;
            string[] p_known = null;
            if (p_openerVt == IVVT_PauseMenu || p_openerVt == IVVT_MainMenu)
                p_known = new string[] { "option_yes_text", "option_no_text" };
            bool p_usedFallback = false;
            if (p_known != null && p_arrN < p_count)
            {
                for (int i = 0; i < p_count && i < p_known.Length; i++)
                    if (p_keys[i] == null) p_keys[i] = p_known[i];
                p_usedFallback = true;
            }

            string p_key = (p_idx >= 0 && p_idx < p_count) ? p_keys[p_idx] : null;
            string p_disp = (p_key != null && stringMap.ContainsKey(p_key)) ? stringMap[p_key] : "";

            string p_msg = name;
            if (p_disp.Length > 0) p_msg += ", " + p_disp + ", " + (p_idx + 1) + ofWord + p_count;
            else if (p_count > 1)  p_msg += ", " + (p_idx + 1) + ofWord + p_count;

            string p_dedup = vt.ToString("x8") + "|" + p_idx + "|" + p_disp;
            if (!force && p_dedup == lastDedup) return false;
            lastDedup = p_dedup;
            if (verbose)
                Console.WriteLine("[dbg] popup vt=" + vt.ToString("x") + " count=" + p_count +
                                  " node='" + p_node + "' idx=" + p_idx +
                                  " arr=0x" + p_labels.ToString("x") + " arrN=" + p_arrN +
                                  " opener=0x" + p_opener.ToString("x") + " openerVt=0x" + p_openerVt.ToString("x") +
                                  " usedFallback=" + p_usedFallback +
                                  " key='" + (p_key ?? "") + "' disp='" + p_disp + "'");
            Speak(p_msg);
            return true;
        }

        // 5. ControlSettingsMenu (SSVS 0x4b6dc4 / IV 0x0053A7A0)
        // The IV class is NOT a SettingsMenu: it keeps no key array, its row labels come from the
        // engine's own static key table (IV_CTRL_KEYTABLE) and its row state from +0x124/+0x128.
        // The VALUE of each row comes from the mapping data (see the IV_CTRL_* consts) and is now
        // spoken as "<label>: <value>"; the row count is the one the engine really builds on screen.
        if (vt == 0x4b6dc4 || vt == IVVT_ControlSettingsMenu)
        {
            bool ivCtrl = (vt == IVVT_ControlSettingsMenu);
            uint selNodePtr = ReadU32(hProc, menu + OFF_MENU_SELNAME);
            string selNode = (selNodePtr >= 0x00010000 && selNodePtr <= 0x7fff0000)
                             ? ReadCString(hProc, selNodePtr, 32) : "";

            // Which pane is being edited: the node name prefix is authoritative
            // ("left_line3" / "right_value2"); otherwise the per-pane flags at +0x174/+0x175.
            int pane;
            if (ivCtrl) pane = CtrlPane(menu, selNode);
            else pane = selNode.StartsWith("right") ? 1 : 0;
            string side = (pane == 1) ? p2Word : p1Word;

            // Row: the node name first (ParseNodeIndex already understands "left_line%d"), then the
            // numeric field FUN_0045dbd0/FUN_0045ec50 animate towards.
            int row = ParseNodeIndex(selNode);
            if (row < 0)
            {
                long rowOff = ivCtrl ? (pane == 1 ? IV_OFF_CTRL_ROW_R : IV_OFF_CTRL_ROW_L)
                                     : (pane == 1 ? 0x90 : 0x8c);
                int v = (int)ReadU32(hProc, menu + rowOff);
                if (v >= 0 && v < 64) row = v;
            }

            int ctrlId = ivCtrl ? CtrlMenuId(menu) : 0;
            int rowTotal = ivCtrl ? CtrlRowTotal(ctrlId) : ControlActionKeys.Length;
            if (rowTotal < 1) rowTotal = 1;
            if (row < 0 || row >= rowTotal) row = 0;

            string action = "";
            if (ivCtrl) action = CtrlRowLabelText(ctrlId, row);
            else
            {
                long tbl = ResolveAddr(IV_CTRL_KEYTABLE);
                uint kp = ReadU32(hProc, tbl + (long)row * 4);
                if (kp >= 0x00010000 && kp <= 0x7fff0000)
                {
                    string k = ReadCString(hProc, kp, 48);
                    // Only a key the engine itself knows is spoken: a raw node name or an
                    // untranslated identifier must never reach the user.
                    if (IsPrintableKey(k)) action = ControlRowLabel(k);
                }
                if (action.Length == 0 && row < ControlActionKeys.Length)
                    action = GetControlActionName(row);
            }

            if (row < 0 || action.Length == 0)
            {
                string c_only = vt.ToString("x8") + "|" + side + "|sem-rotulo";
                if (!force && c_only == lastDedup) return false;
                lastDedup = c_only;
                if (verbose)
                    Console.WriteLine("[dbg] controls SEM ROTULO vt=" + vt.ToString("x") +
                                      " node='" + selNode + "' row=" + row);
                Speak(name);
                return true;
            }

            // Valor da linha: dados do mapeamento (motor FUN_0045e670)
            string val = "";
            string screenVal = "";
            int rawCode = int.MinValue;
            if (ivCtrl) val = CtrlRowValue(ctrlId, pane, row, out screenVal, out rawCode);

            string c_msg = name + ", " + side + ", " + action
                         + ((val.Length > 0) ? ": " + val : "")
                         + ", " + (row + 1) + ofWord + rowTotal;

            string c_dedup = vt.ToString("x8") + "|" + side + "|" + row + "|" + action + "|" + val;
            if (!force && c_dedup == lastDedup) return false;
            lastDedup = c_dedup;
            if (verbose)
                Console.WriteLine("[dbg] controls vt=" + vt.ToString("x") + " side=" + side +
                                  " row=" + row + " linhas=" + rowTotal + " id=" + (ivCtrl ? ctrlId : -1) +
                                  " dev=" + (ivCtrl ? CtrlDeviceType(pane) : -1) +
                                  " codigo=" + rawCode + " valor='" + val + "' tela='" + screenVal + "'" +
                                  " action=" + action + " node='" + selNode + "'");
            Speak(c_msg);
            return true;
        }

        // 6. CommandListMenu (SSVS 0x4b6ac4 / IV 0x0053A5C4)
        if (vt == 0x4b6ac4 || vt == IVVT_CommandListMenu)
        {
            int cl_count = (int)ReadU32(hProc, menu + 0x6c);
            int cl_idx = (int)ReadU32(hProc, menu + 0x70);
            if (cl_count <= 0 || cl_count > 128) cl_count = 1;
            if (cl_idx < 0) cl_idx = 0;
            if (cl_idx >= cl_count) cl_idx = cl_count - 1;

            string cl_title = ReadCString(hProc, ReadU32(hProc, menu + 0x94 + (long)cl_idx * 4), 64);
            string cl_cmd = ReadCString(hProc, ReadU32(hProc, menu + 0xe4 + (long)cl_idx * 4), 128);

            if (cl_title.Length > 0 && stringMap.ContainsKey(cl_title))
                cl_title = stringMap[cl_title];
            if (cl_title.Length == 0) cl_title = moveWord + (cl_idx + 1);

            string formattedCmd = FormatCommandText(cl_cmd);

            string cl_msg;
            if (formattedCmd.Length > 0)
                cl_msg = name + ", " + cl_title + ": " + formattedCmd + ", " + (cl_idx + 1) + ofWord + cl_count;
            else
                cl_msg = name + ", " + cl_title + ", " + (cl_idx + 1) + ofWord + cl_count;

            string cl_dedup = vt.ToString("x8") + "|" + cl_idx + "|" + cl_title + "|" + cl_cmd;
            if (!force && cl_dedup == lastDedup) return false;
            lastDedup = cl_dedup;
            if (verbose) Console.WriteLine("[dbg] commandlist idx=" + cl_idx + " title=" + cl_title + " cmd=" + formattedCmd);
            Speak(cl_msg);
            return true;
        }

        // 7. HiddenCharactersMenu (SSVS 0x4b76b4 / IV 0x0053AF7C)
        if (vt == 0x4b76b4 || vt == IVVT_HiddenCharactersMenu)
        {
            int hc_count = (int)ReadU32(hProc, menu + 0x70);
            int hc_idx = (int)ReadU32(hProc, menu + 0x74);
            if (hc_count <= 0 || hc_count > 16) hc_count = 5;
            if (hc_idx < 0) hc_idx = 0;
            if (hc_idx >= hc_count) hc_idx = hc_count - 1;

            string hc_name = ReadCString(hProc, ReadU32(hProc, menu + 0x7c + (long)hc_idx * 4), 64);
            string hc_code = ReadCString(hProc, ReadU32(hProc, menu + 0xcc + (long)hc_idx * 4), 128);

            if (hc_name.Length > 0 && stringMap.ContainsKey(hc_name))
                hc_name = stringMap[hc_name];
            if (hc_name.Length == 0) hc_name = charWord + (hc_idx + 1);

            string formattedCode = FormatCommandText(hc_code);

            string hc_msg;
            if (formattedCode.Length > 0)
                hc_msg = name + ", " + hc_name + ": " + formattedCode + ", " + (hc_idx + 1) + ofWord + hc_count;
            else
                hc_msg = name + ", " + hc_name + ", " + (hc_idx + 1) + ofWord + hc_count;

            string hc_dedup = vt.ToString("x8") + "|" + hc_idx + "|" + hc_name + "|" + hc_code;
            if (!force && hc_dedup == lastDedup) return false;
            lastDedup = hc_dedup;
            if (verbose) Console.WriteLine("[dbg] hiddenchars idx=" + hc_idx + " name=" + hc_name);
            Speak(hc_msg);
            return true;
        }

        // 8. Standard menus (MainMenu, PauseMenu, PopupMenu, MultiplayerMenu, InGameOptions, ...)
        // The item-key array offset is per class in this port (+0x74 for the plain menus, +0x78
        // for the whole Settings family, ...), so it is LOCATED rather than assumed: we take the
        // offset whose char** entries are keys that exist in Data\strings.txt. A junk pointer
        // (like the Settings family's +0x74 option-row array) fails that test and is skipped.
        long arrOff; uint arr; int count;
        bool haveArr = ResolveLabelArray(hProc, menu, out arrOff, out arr, out count);

        string nodeName = ReadCString(hProc, ReadU32(hProc, menu + OFF_MENU_SELNAME), 64);
        int idx = ParseNodeIndex(nodeName);
        if (idx < 0)
        {
            // no usable "lineN/valueN/barN/choiceN" node name: fall back to an integer field
            for (int c = 0; c < SELIDX_OFF_CANDIDATES.Length && idx < 0; c++)
            {
                uint v = ReadU32(hProc, menu + SELIDX_OFF_CANDIDATES[c]);
                if (v < (uint)count) idx = (int)v;
            }
        }

        string key = haveArr ? LabelKeyAt(hProc, arr, count, idx) : "";
        if (!IsPrintableKey(key)) key = "";
        // Never announce a raw node name ("line1", "underline_copy") as a label. Without a
        // validated key array the only safe fallback is a node name that really is a known key.
        if (key.Length == 0 && IsPrintableKey(nodeName) && stringMap.ContainsKey(nodeName)) key = nodeName;

        // Disambiguation for MultiplayerMenu (0x4b807c)
        string disp;
        if (vt == 0x4b807c)
        {
            count = 4;
            if (currentLang == "en")
            {
                switch (idx)
                {
                    case 0: disp = "Quick Match"; break;
                    case 1: disp = "Create Match (Versus)"; break;
                    case 2: disp = "Create Match (Training)"; break;
                    case 3: disp = "Create Match (Story)"; break;
                    default: disp = (key.Length > 0 && stringMap.ContainsKey(key)) ? stringMap[key] : key; break;
                }
            }
            else if (currentLang == "es")
            {
                switch (idx)
                {
                    case 0: disp = "Buscar Partida"; break;
                    case 1: disp = "Crear Partida (Versus)"; break;
                    case 2: disp = "Crear Partida (Entrenamiento)"; break;
                    case 3: disp = "Crear Partida (Historia)"; break;
                    default: disp = (key.Length > 0 && stringMap.ContainsKey(key)) ? stringMap[key] : key; break;
                }
            }
            else if (currentLang == "ja")
            {
                switch (idx)
                {
                    case 0: disp = "クイックマッチ"; break;
                    case 1: disp = "マッチ作成 (VS)"; break;
                    case 2: disp = "マッチ作成 (トレーニング)"; break;
                    case 3: disp = "マッチ作成 (ストーリー)"; break;
                    default: disp = (key.Length > 0 && stringMap.ContainsKey(key)) ? stringMap[key] : key; break;
                }
            }
            else // pt
            {
                switch (idx)
                {
                    case 0: disp = "Procurar Partida"; break;
                    case 1: disp = "Criar Partida (Versus)"; break;
                    case 2: disp = "Criar Partida (Treino)"; break;
                    case 3: disp = "Criar Partida (História)"; break;
                    default: disp = (key.Length > 0 && stringMap.ContainsKey(key)) ? stringMap[key] : key; break;
                }
            }
        }
        else
        {
            if (key.Length > 0 && stringMap.ContainsKey(key)) disp = stringMap[key];
            else disp = key;
        }

        bool haveCount = (count >= 1 && count <= 256 && idx >= 0 && idx < count);
        // IV: uma lista cujos itens sao as proprias chaves de idioma do motor e a tela de
        // IDIOMAS (PopupMenu/StandardMenu aberto a partir de Configuracoes do Sistema). Sem
        // isto o titulo cairia no "Menu" generico do vtable desconhecido.
        if (IsLanguageKey(key) && (name == null || name == L4("Menu", "Menú", "メニュー", "Menu")))
            name = LanguageTitle();
        string msg;
        if (haveCount)
            msg = name + ", " + disp + ", " + (idx + 1) + ofWord + count;
        else if (disp.Length > 0)
            msg = name + ", " + disp;
        else
            msg = name;

        string dedup = vt.ToString("x8") + "|" + idx + "|" + key + "|" + arrOff;
        if (!force && dedup == lastDedup) return false;
        lastDedup = dedup;

        if (verbose)
        {
            Console.WriteLine("[dbg] mgr=0x" + mgr.ToString("x") + " menu=0x" + menu.ToString("x") +
                              " vt=0x" + vt.ToString("x") + " arr=0x" + arr.ToString("x") +
                              " count=" + count + " idx=" + idx + " key='" + key + "'");
        }

        Speak(msg);
        return true;
    }

    static void SpeakMenuName()
    {
        if (hProc == IntPtr.Zero) return;
        if (mgr == 0) mgr = ScanForManager(hProc);
        if (mgr == 0) return;
        uint menu = ReadU32(hProc, mgr + OFF_MGR_MENU);
        if (menu < 0x10000) return;
        uint vt = ReadU32(hProc, menu);
        Speak(MenuName(vt));
    }

    // ---- hotkey edge detection ----
    static bool Edge(int vk)
    {
        if (vk < 0 || vk > 255) return false;
        short st = GetAsyncKeyState(vk);
        bool down = (st & 0x8000) != 0;
        bool pressed = down && !keyWasDown[vk];
        keyWasDown[vk] = down;
        return pressed;
    }

    // ---- announcer chain: NVDA ONLY (Prism NVDA backend or direct nvdaControllerClient) ----
    static bool InitPrism()
    {
        try
        {
            if (prismCtx == IntPtr.Zero)
            {
                prismCtx = prism_init(IntPtr.Zero);
                if (prismCtx == IntPtr.Zero) return false;
            }
            if (prismBackend == IntPtr.Zero)
            {
                prismBackend = prism_registry_acquire_best(prismCtx);
                if (prismBackend != IntPtr.Zero)
                {
                    IntPtr np = prism_backend_name(prismBackend);
                    prismBackendName = Marshal.PtrToStringAnsi(np);
                    Log("Prism acquired backend: " + prismBackendName);
                    // REQUIRE NVDA: if Prism did not acquire NVDA (e.g. OneCore or SAPI), reject it immediately
                    if (!string.Equals(prismBackendName, "NVDA", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Rejecting non-NVDA Prism backend: " + prismBackendName + " (NVDA ONLY enforced)");
                        prismBackend = IntPtr.Zero;
                        return false;
                    }
                    prism_backend_initialize(prismBackend);
                }
            }
            return prismBackend != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log("InitPrism error: " + ex.Message);
            return false;
        }
    }

    static bool TryPrism(string text)
    {
        try
        {
            if (!InitPrism()) return false;
            byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
            int r = prism_backend_speak(prismBackend, utf8, true);
            return r == 0;
        }
        catch { return false; }
    }

    static void Speak(string text)
    {
        if (text == null || text.Length == 0) return;
        Console.WriteLine("SAY: " + text);
        Log("SPEAK: " + text);
        if (mute) return;

        // User requested ONLY NVDA: prioritize direct nvdaControllerClient (pure UTF-16 Unicode),
        // with Prism (NVDA backend, UTF-8) as secondary.
        // Never fall back to SAPI or OneCore unless explicitly passed -Speaker sapi
        if (speakerMode == "nvda" || speakerMode == "auto" || speakerMode == "prism")
        {
            if (TryNvda(text)) { Log("[fala -> nvdaControllerClient]"); return; }
            if (TryPrism(text)) { Log("[fala -> prism (NVDA)]"); return; }
            
            if (speakerMode == "auto")
            {
                Log("[NVDA indisponivel -> tentando fallback SAPI/UIA]");
                if (TrySapi(text)) { Log("[fala -> SAPI fallback]"); return; }
                if (TryUia(text)) { Log("[fala -> UIA fallback]"); return; }
            }
            Log("[Aviso: nenhum sintetizador de voz respondeu para falar]");
            return;
        }

        if (speakerMode == "sapi")
        {
            if (TrySapi(text)) { Log("[fala -> sapi]"); return; }
            return;
        }
        if (speakerMode == "uia")
        {
            if (TryUia(text)) { Log("[fala -> uia]"); return; }
            return;
        }
    }

    static bool TryNvda(string text)
    {
        try
        {
            int test = nvdaController_testIfRunning();
            if (test != 0) return false;
            nvdaController_cancelSpeech();
            int r = nvdaController_speakText(text);
            return r == 0;
        }
        catch (Exception ex)
        {
            Log("TryNvda falhou: " + ex.Message);
            return false;
        }
    }

    static bool TryUia(string text)
    {
        try
        {
            if (uiaProvider == IntPtr.Zero)
            {
                uiaForm = new Form();
                uiaForm.ShowInTaskbar = false;
                uiaForm.FormBorderStyle = FormBorderStyle.None;
                uiaForm.StartPosition = FormStartPosition.Manual;
                uiaForm.Location = new System.Drawing.Point(-32000, -32000);
                uiaForm.Size = new System.Drawing.Size(1, 1);
                IntPtr hwnd = uiaForm.Handle;
                IntPtr prov;
                int hr = UiaHostProviderFromHwnd(hwnd, out prov);
                if (hr != 0 || prov == IntPtr.Zero) return false;
                uiaProvider = prov;
            }
            // NotificationKind=4 (Other), NotificationProcessing=1 (ImportantMostRecent)
            int hr2 = UiaRaiseNotificationEvent(uiaProvider, 4, 1, text, "ssvs");
            return hr2 == 0;
        }
        catch { return false; }
    }

    static bool TrySapi(string text)
    {
        try
        {
            if (sapi == null)
            {
                Type t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t == null) return false;
                sapi = Activator.CreateInstance(t);
            }
            // flags 3 = SVSFlagsAsync(1) | SVSFPurgeBeforeSpeak(2)
            sapi.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, sapi, new object[] { text, 3 });
            return true;
        }
        catch { return false; }
    }
}
