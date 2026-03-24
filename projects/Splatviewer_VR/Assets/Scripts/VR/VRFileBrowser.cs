// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// World-space VR file browser for browsing the file system and loading .ply/.spz/.sog/.spx splat files.
///
/// Layout: left 1/4 navigation list, right 3/4 preview panel (thumbnail or filename placeholder).
/// Animation folders (containing 2+ splat files) are shown with a ▶ play icon; selecting them starts movie mode.
///
/// VR Controls:
///   Left Y (secondaryButton)  → toggle browser open/close
///   Left or right stick up/down → navigate list
///   Left or right trigger     → select (enter folder / load file / play animation)
///   Right B (secondaryButton) → go to parent directory
///
/// Desktop fallback:
///   Esc / Tab     → toggle browser
///   WASD / Arrows → navigate list
///   Enter         → select
///   Backspace     → go to parent
/// </summary>
public class VRFileBrowser : MonoBehaviour
{
    const string FavoritesPrefsKey = "Splatviewer.VRFileBrowser.Favorites";

    [Header("Setup")]
    [Tooltip("RuntimeSplatLoader to load selected files into.")]
    public RuntimeSplatLoader loader;

    [Tooltip("Optional SplatCycler — its folder will be updated when a file is loaded from the browser.")]
    public SplatCycler cycler;

    [Tooltip("Optional WorldGrabManipulator — world is reset before each file load.")]
    public WorldGrabManipulator worldGrab;

    [Tooltip("Initial folder path. Leave empty to start at drive list.")]
    public string startPath = "";

    [Header("Placement")]
    [Tooltip("Distance in front of the user when the browser opens.")]
    public float spawnDistance = 1.5f;

    /// <summary>True when the browser panel is visible and consuming right-hand input.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Remains true for one extra frame after closing, to prevent button leak to other scripts.</summary>
    public bool WasOpenThisFrame { get; private set; }

    // ── Layout constants ──────────────────────────────────────────────────────

    const int CW = 1200, CH = 650;
    const float SCALE = 0.001f;
    const int ROWS = 14;
    const int ROW_H = 38;
    const int PATH_H = 36;
    const int HINT_H = 30;
    const int PAD = 10;
    const int FAVORITES_W = 250;
    const int LIST_GAP = 12;
    const int FONT_ENTRY = 22;
    const int FONT_PATH = 18;
    const int FONT_HINT = 16;

    // Split layout: left nav ≈1/4, right preview ≈3/4
    const int NAV_W = 360;
    const int SEP_X = NAV_W + PAD;
    const int PREVIEW_X = SEP_X + 6;
    const int PREVIEW_W = CW - PREVIEW_X - PAD;

    // ── Colors ────────────────────────────────────────────────────────────────

    static readonly Color COL_BG      = new Color(0.08f, 0.08f, 0.10f, 1.00f);
    static readonly Color COL_SEL     = new Color(0.20f, 0.40f, 0.85f, 1.00f);
    static readonly Color COL_ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color COL_CURRENT = new Color(0.55f, 1.00f, 0.75f);
    static readonly Color COL_CURRENT_BG = new Color(0.18f, 0.42f, 0.24f, 0.55f);
    static readonly Color COL_FAVORITE = new Color(0.95f, 0.82f, 0.42f);
    static readonly Color COL_FAVORITE_BG = new Color(0.42f, 0.32f, 0.12f, 0.45f);
    static readonly Color COL_DIR     = new Color(1f, 0.88f, 0.40f);
    static readonly Color COL_ANIM    = new Color(0.40f, 0.90f, 0.50f);
    static readonly Color COL_FILE    = Color.white;
    static readonly Color COL_PATH    = new Color(0.65f, 0.65f, 0.65f);
    static readonly Color COL_HINT    = new Color(0.45f, 0.45f, 0.45f);
    static readonly Color COL_CLEAR   = new Color(0f, 0f, 0f, 0f);
    static readonly Color COL_PREVIEW_BG = new Color(0.06f, 0.06f, 0.08f, 1f);

    // ── State ─────────────────────────────────────────────────────────────────

    string _currentPath;
    readonly List<Entry> _entries = new List<Entry>();
    readonly List<string> _favoriteFolders = new List<string>();
    int _sel;
    int _scroll;
    int _favoriteSel;
    int _favoriteScroll;

    enum BrowserPane { Favorites, Files }
    BrowserPane _activePane = BrowserPane.Files;

    // ── UI objects ────────────────────────────────────────────────────────────

    GameObject _root;
    Text _pathText;
    Text _fpsText;
    Text _hintText;
    Text _helpText;
    Text[] _favoriteTexts;
    Text[] _rowTexts;
    Image[] _favoriteBgs;
    Image[] _rowBgs;
    // Preview panel (right side)
    GameObject _previewPanel;
    RawImage _thumbnailImage;
    Texture2D _thumbnailTex;
    Text _previewNameText;
    Text _previewPlayIcon;
    string _lastThumbnailPath;
    string _preferredFilePath;
    static Font _font;

    // ── Input state ───────────────────────────────────────────────────────────

    float _stickCD;
    bool _trigReady = true;
    bool _toggleReady = true;
    bool _backReady = true;
    bool _preloadToggleReady = true;
    bool _movieBtnReady = true;
    bool _favoriteToggleReady = true;
    float _fpsAdjustCD;

    // Movie mode
    enum MovieState { Idle, Loading, Playing }
    MovieState _movieState = MovieState.Idle;
    int _movieLoadedCount;
    int _movieTotalCount;
    float _smoothedFps;

    struct Entry
    {
        public string name;
        public string path;
        public bool isDir;
        public bool isAnimation; // folder containing 2+ splat files
        public int animFrameCount; // number of splat files in animation folder
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (loader == null) loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (cycler == null) cycler = FindAnyObjectByType<SplatCycler>();
        if (worldGrab == null) worldGrab = FindAnyObjectByType<WorldGrabManipulator>();

        _currentPath = string.IsNullOrEmpty(startPath) ? null : startPath;
        LoadFavorites();

        // Cache a font reference
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);

        BuildUI();
        _root.SetActive(false);
        ApplyHighQualitySettings();
    }

    void Update()
    {
        // Clear the one-frame guard from previous frame
        WasOpenThisFrame = IsOpen;

        UpdateFpsDisplay();

        // Movie loading pump — runs even when browser is closed
        if (_movieState == MovieState.Loading)
        {
            bool done = cycler != null && cycler.PumpMovieLoad();
            if (done)
            {
                if (cycler != null && cycler.IsMovieReady)
                {
                    _movieState = MovieState.Playing;
                    cycler.StartMoviePlayback();
                    Debug.Log("[VRFileBrowser] Movie loading complete — playback started");
                    if (IsOpen) ToggleBrowser(); // close browser when playback starts
                }
                else
                {
                    _movieState = MovieState.Idle;
                    string error = cycler != null && cycler.loader != null ? cycler.loader.MovieLastError : null;
                    if (!string.IsNullOrEmpty(error))
                    {
                        Debug.LogWarning($"[VRFileBrowser] Movie loading failed: {error}");
                        if (_pathText != null)
                            _pathText.text = $"Movie load failed: {error}";
                    }
                    else
                    {
                        Debug.LogWarning("[VRFileBrowser] Movie loading failed");
                    }
                }
                UpdateHelpText();
            }
            // Block all other input during loading
            if (_movieState == MovieState.Loading)
            {
                UpdateHelpText(); // refresh progress
                return;
            }
        }

        // Movie FPS adjustment — works even when browser is closed during playback
        if (_movieState == MovieState.Playing)
            HandleMovieFpsAdjust();

        // Movie stop — Left Y or M key stops playback
        if (_movieState == MovieState.Playing)
        {
            bool stopBtn = false;
            if (XRSettings.isDeviceActive)
                stopBtn = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton); // Y to stop
            else
                stopBtn = Input.GetKeyDown(KeyCode.M);

            if (stopBtn && _movieBtnReady)
            {
                _movieBtnReady = false;
                StopMovieMode();
                return;
            }
            else if (!stopBtn)
                _movieBtnReady = true;
        }

        // Toggle: right stick click (VR) or Esc/Tab (desktop)
        bool toggleBtn = false;
        if (XRSettings.isDeviceActive)
            toggleBtn = ReadStickClick(XRNode.RightHand);
        if (toggleBtn && _toggleReady) { ToggleBrowser(); _toggleReady = false; }
        else if (!toggleBtn) _toggleReady = true;

        if (!XRSettings.isDeviceActive
            && (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab)))
            ToggleBrowser();

        if (!IsOpen) return;

        HandleNavigation();
        HandleSelect();
        HandleBack();
        HandleFavoriteToggle();
        HandlePreloadToggle();
        HandleMovieButton();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ToggleBrowser()
    {
        // Block closing while movie is loading
        if (IsOpen && _movieState == MovieState.Loading)
            return;

        IsOpen = !IsOpen;
        _root.SetActive(IsOpen);
        if (IsOpen)
        {
            if (loader != null && !string.IsNullOrWhiteSpace(loader.CurrentFilePath))
            {
                _currentPath = Path.GetDirectoryName(loader.CurrentFilePath);
                _preferredFilePath = loader.CurrentFilePath;
            }

            PositionInFront();
            Navigate(_currentPath);
        }

        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = IsOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = IsOpen;
        }
    }

    public void SetCurrentFolder(string path)
    {
        _currentPath = string.IsNullOrWhiteSpace(path) ? null : path;
        startPath = _currentPath ?? string.Empty;
        _preferredFilePath = null;

        if (IsOpen)
            Navigate(_currentPath);
    }

    public void SetCurrentFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            SetCurrentFolder(null);
            return;
        }

        _currentPath = Path.GetDirectoryName(filePath);
        startPath = _currentPath ?? string.Empty;
        _preferredFilePath = filePath;

        if (IsOpen)
            Navigate(_currentPath);
    }

    // ── Input Handling ────────────────────────────────────────────────────────

    void HandleNavigation()
    {
        // Left/right switches between favorites and file list.
        float ry = 0f;
        float rx = 0f;
        if (XRSettings.isDeviceActive)
        {
            ry = ReadNavigationY();
            rx = ReadNavigationX();
        }
        else
        {
            if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W))   ry =  1f;
            if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) ry = -1f;
            if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) rx = -1f;
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) rx = 1f;
        }

        _stickCD -= Time.deltaTime;
        if (Mathf.Abs(rx) > 0.5f && Mathf.Abs(rx) >= Mathf.Abs(ry) && _stickCD <= 0f)
        {
            _activePane = rx < 0f ? BrowserPane.Favorites : BrowserPane.Files;
            UpdateRows();
            _stickCD = 0.18f;
        }
        else if (Mathf.Abs(ry) > 0.5f && _stickCD <= 0f)
        {
            if (_activePane == BrowserPane.Favorites)
            {
                if (_favoriteFolders.Count > 0)
                {
                    _favoriteSel += ry < 0f ? 1 : -1;
                    _favoriteSel = Mathf.Clamp(_favoriteSel, 0, _favoriteFolders.Count - 1);
                    EnsureFavoriteVisible();
                }
            }
            else if (_entries.Count > 0)
            {
                _sel += ry < 0f ? 1 : -1;
                _sel = Mathf.Clamp(_sel, 0, _entries.Count - 1);
                EnsureVisible();
            }

            UpdateRows();
            _stickCD = 0.18f;
        }
        else if (Mathf.Abs(ry) <= 0.3f && Mathf.Abs(rx) <= 0.3f)
        {
            _stickCD = 0f;
        }
    }

    void HandleSelect()
    {
        bool trig = false;
        if (XRSettings.isDeviceActive)
            trig = ReadTrigger(XRNode.LeftHand)
                || ReadTrigger(XRNode.RightHand)
                || ReadButton(XRNode.RightHand, CommonUsages.primaryButton);
        else
            trig = Input.GetKeyDown(KeyCode.Return);

        if (trig && _trigReady)
        {
            _trigReady = false;
            if (_activePane == BrowserPane.Favorites)
                SelectFavorite();
            else
                SelectCurrent();
        }
        else if (!trig)
        {
            _trigReady = true;
        }
    }

    void HandleBack()
    {
        bool b = false;
        if (XRSettings.isDeviceActive)
            b = ReadButton(XRNode.RightHand, CommonUsages.secondaryButton);
        else
            b = Input.GetKeyDown(KeyCode.Backspace);

        if (b && _backReady)
        {
            _backReady = false;
            if (_activePane == BrowserPane.Favorites)
            {
                _activePane = BrowserPane.Files;
                UpdateRows();
            }
            else
            {
                GoUp();
            }
        }
        else if (!b)
        {
            _backReady = true;
        }
    }

    void HandleFavoriteToggle()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
        {
            s_devices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, s_devices);
            if (s_devices.Count > 0)
                s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxisClick, out pressed);
        }
        else
        {
            pressed = Input.GetKeyDown(KeyCode.F);
        }

        if (pressed && _favoriteToggleReady)
        {
            _favoriteToggleReady = false;
            if (_activePane == BrowserPane.Favorites)
                RemoveSelectedFavorite();
            else
                AddCurrentFolderToFavorites();
        }
        else if (!pressed)
        {
            _favoriteToggleReady = true;
        }
    }

    void HandlePreloadToggle()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
            pressed = ReadButton(XRNode.LeftHand, CommonUsages.primaryButton); // X on left controller
        else
            pressed = Input.GetKeyDown(KeyCode.P);

        if (pressed && _preloadToggleReady)
        {
            _preloadToggleReady = false;
            if (cycler != null)
            {
                cycler.preloadUpcomingFiles = !cycler.preloadUpcomingFiles;
                cycler.ApplyPreloadBudget();
                if (cycler.preloadUpcomingFiles)
                    cycler.RefreshPreloadWindow();
                else if (cycler.loader != null)
                    cycler.loader.SetPreloadTargets(Array.Empty<string>());
                Debug.Log($"[VRFileBrowser] Preloading {(cycler.preloadUpcomingFiles ? "ON" : "OFF")}");
            }
            UpdateHelpText();
        }
        else if (!pressed)
        {
            _preloadToggleReady = true;
        }
    }

    void HandleMovieButton()
    {
        bool pressed = false;
        if (XRSettings.isDeviceActive)
        {
            // Use left Y button for movie mode start (while browser is open)
            pressed = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton);
        }
        else
        {
            pressed = Input.GetKeyDown(KeyCode.M);
        }

        if (pressed && _movieBtnReady)
        {
            _movieBtnReady = false;
            if (_movieState == MovieState.Idle)
                StartMovieMode();
        }
        else if (!pressed)
        {
            _movieBtnReady = true;
        }
    }

    void ApplyHighQualitySettings()
    {
        var gs = FindAnyObjectByType<GaussianSplatRenderer>();
        if (gs == null) return;

        gs.m_SHOrder = 3;
        gs.m_AlphaClipThreshold = 1f / 255f;
        gs.m_SplatEdgeSharpness = 1.0f;
    }

    void HandleMovieFpsAdjust()
    {
        if (cycler == null) return;

        float rx = 0f;
        if (XRSettings.isDeviceActive)
        {
            // Use left stick X for FPS adjustment
            s_devices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, s_devices);
            if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
                rx = v.x;
        }
        else
        {
            if (Input.GetKey(KeyCode.RightArrow)) rx = 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) rx = -1f;
        }

        _fpsAdjustCD -= Time.deltaTime;
        if (Mathf.Abs(rx) > 0.5f && _fpsAdjustCD <= 0f)
        {
            cycler.AdjustMovieFps(rx > 0 ? 1 : -1);
            _fpsAdjustCD = 0.15f;
        }
        else if (Mathf.Abs(rx) <= 0.3f)
        {
            _fpsAdjustCD = 0f;
        }
    }

    void StartMovieMode()
    {
        // Sync the cycler to the browser's current folder so we always load
        // from the directory the user is actually looking at, not the last
        // folder a file was selected from.
        if (cycler != null && !string.IsNullOrEmpty(_currentPath))
        {
            cycler.splatFolder = _currentPath;
            cycler.ScanFolder();
        }

        if (cycler == null || cycler.Files.Count == 0)
        {
            Debug.LogWarning("[VRFileBrowser] No files loaded to start movie mode");
            return;
        }

        var (fits, estMB, availMB) = cycler.CheckMovieFit();
        if (!fits)
        {
            Debug.LogError($"[VRFileBrowser] Movie mode: not enough RAM! Need ~{estMB}MB, available ~{availMB}MB");
            _pathText.text = $"Not enough RAM! Need ~{estMB}MB, have ~{availMB}MB";
            return;
        }

        _movieLoadedCount = 0;
        _movieTotalCount = cycler.Files.Count;
        _movieState = MovieState.Loading;

        bool started = cycler.BeginMovieLoad((loaded, total) =>
        {
            _movieLoadedCount = loaded;
            _movieTotalCount = total;
        });

        if (!started)
        {
            _movieState = MovieState.Idle;
            Debug.LogError("[VRFileBrowser] Failed to start movie loading");
        }
        else
        {
            Debug.Log($"[VRFileBrowser] Movie loading started: {_movieTotalCount} frames (~{estMB}MB)");
        }

        UpdateHelpText();
    }

    void StartMovieModeFromFolder(string folderPath)
    {
        if (cycler == null) return;

        cycler.splatFolder = folderPath;
        cycler.ScanFolder();

        if (cycler.Files.Count == 0)
        {
            Debug.LogWarning($"[VRFileBrowser] No splat files in animation folder: {folderPath}");
            return;
        }

        var (fits, estMB, availMB) = cycler.CheckMovieFit();
        if (!fits)
        {
            Debug.LogError($"[VRFileBrowser] Animation: not enough RAM! Need ~{estMB}MB, available ~{availMB}MB");
            _pathText.text = $"Not enough RAM! Need ~{estMB}MB, have ~{availMB}MB";
            _pathText.color = new Color(1f, 0.3f, 0.3f);
            return;
        }

        _movieLoadedCount = 0;
        _movieTotalCount = cycler.Files.Count;
        _movieState = MovieState.Loading;

        bool started = cycler.BeginMovieLoad((loaded, total) =>
        {
            _movieLoadedCount = loaded;
            _movieTotalCount = total;
        });

        if (!started)
        {
            _movieState = MovieState.Idle;
            Debug.LogError("[VRFileBrowser] Failed to start animation loading");
        }
        else
        {
            Debug.Log($"[VRFileBrowser] Animation loading started from {Path.GetFileName(folderPath)}: {_movieTotalCount} frames (~{estMB}MB)");
        }

        UpdateHelpText();
    }

    void StopMovieMode()
    {
        if (cycler != null)
            cycler.StopMovie();
        _movieState = MovieState.Idle;
        _movieLoadedCount = 0;
        _movieTotalCount = 0;
        UpdateHelpText();
    }

    void LoadFavorites()
    {
        _favoriteFolders.Clear();

        string raw = PlayerPrefs.GetString(FavoritesPrefsKey, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (string folder in raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = folder.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && Directory.Exists(trimmed) && !_favoriteFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                _favoriteFolders.Add(trimmed);
        }
    }

    void SaveFavorites()
    {
        PlayerPrefs.SetString(FavoritesPrefsKey, string.Join("\n", _favoriteFolders));
        PlayerPrefs.Save();
    }

    void AddCurrentFolderToFavorites()
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !Directory.Exists(_currentPath))
            return;

        if (_favoriteFolders.Contains(_currentPath, StringComparer.OrdinalIgnoreCase))
            return;

        _favoriteFolders.Add(_currentPath);
        _favoriteFolders.Sort(StringComparer.OrdinalIgnoreCase);
        _favoriteSel = Mathf.Clamp(_favoriteFolders.FindIndex(folder => string.Equals(folder, _currentPath, StringComparison.OrdinalIgnoreCase)), 0, Mathf.Max(0, _favoriteFolders.Count - 1));
        EnsureFavoriteVisible();
        SaveFavorites();
        UpdateRows();
    }

    void RemoveSelectedFavorite()
    {
        if (_favoriteSel < 0 || _favoriteSel >= _favoriteFolders.Count)
            return;

        _favoriteFolders.RemoveAt(_favoriteSel);
        _favoriteSel = Mathf.Clamp(_favoriteSel, 0, Mathf.Max(0, _favoriteFolders.Count - 1));
        EnsureFavoriteVisible();
        SaveFavorites();
        UpdateRows();
    }

    // ── File System ───────────────────────────────────────────────────────────

    void Navigate(string path)
    {
        _currentPath = path;
        _entries.Clear();
        _sel = 0;
        _scroll = 0;

        if (string.IsNullOrEmpty(_currentPath))
        {
            // List drives
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    string label = "";
                    try { label = d.VolumeLabel; } catch { }
                    _entries.Add(new Entry
                    {
                        name = string.IsNullOrEmpty(label)
                            ? d.Name.TrimEnd('\\')
                            : $"{d.Name.TrimEnd('\\')}  ({label})",
                        path = d.RootDirectory.FullName,
                        isDir = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VRFileBrowser] Drive list error: {ex.Message}");
            }
        }
        else
        {
            // Parent entry
            var parent = Directory.GetParent(_currentPath);
            _entries.Add(new Entry { name = "..", path = parent?.FullName, isDir = true });

            try
            {
                foreach (var dir in Directory.GetDirectories(_currentPath)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    int splatCount = CountSplatFiles(dir);
                    _entries.Add(new Entry
                    {
                        name = Path.GetFileName(dir),
                        path = dir,
                        isDir = true,
                        isAnimation = splatCount >= 2,
                        animFrameCount = splatCount
                    });
                }

                foreach (var file in Directory.GetFiles(_currentPath)
                    .Where(f =>
                    {
                        return RuntimeSplatLoader.IsSupportedFileExtension(f);
                    })
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    _entries.Add(new Entry
                    {
                        name = Path.GetFileName(file),
                        path = file,
                        isDir = false
                    });
                }
            }
            catch (UnauthorizedAccessException) { /* skip protected dirs */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VRFileBrowser] Cannot list {_currentPath}: {ex.Message}");
            }
        }

        TrySelectPreferredFile();

        UpdatePath();
        UpdateRows();
    }

    /// <summary>Count splat files in a directory (non-recursive). Caps at 9999 to avoid slow scans.</summary>
    static int CountSplatFiles(string dirPath)
    {
        try
        {
            int count = 0;
            foreach (var f in Directory.EnumerateFiles(dirPath))
            {
                if (RuntimeSplatLoader.IsSupportedFileExtension(f))
                {
                    count++;
                    if (count >= 9999) break;
                }
            }
            return count;
        }
        catch { return 0; }
    }

    void TrySelectPreferredFile()
    {
        if (string.IsNullOrEmpty(_preferredFilePath) || string.IsNullOrEmpty(_currentPath))
            return;

        string preferredFolder = Path.GetDirectoryName(_preferredFilePath);
        if (!string.Equals(preferredFolder, _currentPath, StringComparison.OrdinalIgnoreCase))
            return;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].isDir)
                continue;

            if (string.Equals(_entries[i].path, _preferredFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _sel = i;
                EnsureVisible();
                return;
            }
        }
    }

    void SelectCurrent()
    {
        if (_sel < 0 || _sel >= _entries.Count) return;
        var entry = _entries[_sel];

        if (entry.isDir && entry.isAnimation)
        {
            // Animation folder — start movie mode from this folder
            if (_movieState != MovieState.Idle)
                StopMovieMode();

            if (worldGrab != null)
                worldGrab.ResetWorld();

            StartMovieModeFromFolder(entry.path);
        }
        else if (entry.isDir)
        {
            Navigate(entry.path); // null path → drive list
        }
        else
        {
            if (loader != null)
            {
                // Stop any active movie playback/loading before loading a new file
                if (_movieState != MovieState.Idle)
                    StopMovieMode();

                // Pre-flight RAM check
                long estimatedBytes = RuntimeSplatLoader.EstimateAssetBytes(entry.path);
                long availableBytes = (long)SystemInfo.systemMemorySize * 1024L * 1024L * 80L / 100L;
                if (estimatedBytes > availableBytes)
                {
                    long estMB = estimatedBytes / (1024 * 1024);
                    long availMB = availableBytes / (1024 * 1024);
                    Debug.LogWarning($"[VRFileBrowser] File may exceed available RAM: ~{estMB}MB needed, ~{availMB}MB available");
                    _pathText.text = $"WARNING: ~{estMB}MB needed, only ~{availMB}MB available!";
                    _pathText.color = new Color(1f, 0.7f, 0.2f);
                    // Still attempt the load — the warning is informational
                }

                _preferredFilePath = entry.path;

                // Reset world to neutral before loading new scene
                if (worldGrab != null)
                    worldGrab.ResetWorld();

                try
                {
                    bool ok = loader.LoadFile(entry.path);
                    if (ok)
                    {
                        // Sync SplatCycler to the loaded file so B/A cycling continues from here
                        if (cycler != null && !string.IsNullOrEmpty(_currentPath))
                        {
                            cycler.splatFolder = _currentPath;
                            cycler.ScanFolder();
                            // Find the loaded file's index in the cycler's list
                            string fileName = Path.GetFileName(entry.path);
                            for (int i = 0; i < cycler.Files.Count; i++)
                            {
                                if (Path.GetFileName(cycler.Files[i]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    cycler.SetIndex(i);
                                    break;
                                }
                            }
                        }

                        // Reset camera to initial viewpoint
                        var rig = FindAnyObjectByType<VRRig>();
                        if (rig != null) rig.ResetToSpawnPoint(loader != null ? loader.targetRenderer : null);

                        ToggleBrowser(); // close after loading
                    }
                    else
                    {
                        _pathText.text = $"Failed to load: {entry.name}";
                        _pathText.color = new Color(1f, 0.3f, 0.3f);
                    }
                }
                catch (OutOfMemoryException)
                {
                    Debug.LogError($"[VRFileBrowser] Out of memory loading {entry.name}");
                    _pathText.text = $"OUT OF MEMORY — file too large: {entry.name}";
                    _pathText.color = new Color(1f, 0.3f, 0.3f);
                    System.GC.Collect();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VRFileBrowser] Error loading {entry.name}: {ex.Message}");
                    _pathText.text = $"Error: {ex.Message}";
                    _pathText.color = new Color(1f, 0.3f, 0.3f);
                }
            }
        }
    }

    void SelectFavorite()
    {
        if (_favoriteSel < 0 || _favoriteSel >= _favoriteFolders.Count)
            return;

        string favoritePath = _favoriteFolders[_favoriteSel];
        if (!Directory.Exists(favoritePath))
        {
            RemoveSelectedFavorite();
            return;
        }

        _activePane = BrowserPane.Files;
        Navigate(favoritePath);
    }

    void GoUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return; // already at drives
        var parent = Directory.GetParent(_currentPath);
        Navigate(parent?.FullName);
    }

    // ── UI Building ───────────────────────────────────────────────────────────

    void BuildUI()
    {
        // World-space Canvas
        _root = new GameObject("VRFileBrowser");
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _root.AddComponent<CanvasScaler>();

        var canvasRT = _root.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(CW, CH);
        _root.transform.localScale = Vector3.one * SCALE;

        // Background
        var bg = MakeChild(_root.transform, "BG");
        bg.AddComponent<Image>().color = COL_BG;
        Stretch(bg);

        // ── Layout from top down ──
        float y = -PAD;

        // Path bar (full width)
        _pathText = MakeText(bg.transform, "Path", "", FONT_PATH, COL_PATH,
            PAD + 4, y, CW - 160f, PATH_H);
        _fpsText = MakeText(bg.transform, "FPS", "", FONT_PATH, COL_CURRENT,
            CW - 140, y, 120, PATH_H, TextAnchor.MiddleRight);
        y -= PATH_H;

        // Horizontal separator under path
        var sep = MakeChild(bg.transform, "Sep");
        sep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        SetRect(sep, PAD, y, CW - PAD * 2, 1);
        y -= 4;

        float contentY = y; // remember where content starts
        float filesX = PAD + FAVORITES_W + LIST_GAP;
        float filesW = NAV_W - FAVORITES_W - LIST_GAP;

        // Favorites / files divider
        var divider = MakeChild(bg.transform, "PaneDivider");
        divider.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
        SetRect(divider, PAD + FAVORITES_W + (LIST_GAP * 0.5f), contentY, 1, ROWS * ROW_H);

        // ── Favorites column (left) ──
        _favoriteBgs = new Image[ROWS];
        _favoriteTexts = new Text[ROWS];
        float favY = contentY;
        for (int i = 0; i < ROWS; i++)
        {
            var favBg = MakeChild(bg.transform, $"FavoriteBg{i}");
            _favoriteBgs[i] = favBg.AddComponent<Image>();
            _favoriteBgs[i].color = COL_CLEAR;
            SetRect(favBg, PAD, favY, FAVORITES_W, ROW_H);

            _favoriteTexts[i] = MakeText(bg.transform, $"Favorite{i}", "", FONT_ENTRY, COL_FAVORITE,
                PAD + 10, favY, FAVORITES_W - 20, ROW_H);
            _favoriteTexts[i].resizeTextForBestFit = true;
            _favoriteTexts[i].resizeTextMinSize = 12;
            _favoriteTexts[i].resizeTextMaxSize = FONT_ENTRY;
            favY -= ROW_H;
        }

        // ── File list column (middle) ──
        _rowBgs  = new Image[ROWS];
        _rowTexts = new Text[ROWS];
        float rowY = contentY;
        for (int i = 0; i < ROWS; i++)
        {
            var rowBg = MakeChild(bg.transform, $"RowBg{i}");
            _rowBgs[i] = rowBg.AddComponent<Image>();
            _rowBgs[i].color = COL_CLEAR;
            SetRect(rowBg, filesX, rowY, filesW, ROW_H);

            _rowTexts[i] = MakeText(bg.transform, $"Row{i}", "", FONT_ENTRY, COL_FILE,
                filesX + 8, rowY, filesW - 16, ROW_H);
            rowY -= ROW_H;
        }

        // Vertical separator between file list and preview
        float sepHeight = ROWS * ROW_H;
        var vSep = MakeChild(bg.transform, "VSep");
        vSep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);
        SetRect(vSep, SEP_X, contentY, 1, sepHeight);

        // ── Right side: preview panel ──
        _previewPanel = MakeChild(bg.transform, "PreviewPanel");
        var previewBg = _previewPanel.AddComponent<Image>();
        previewBg.color = COL_PREVIEW_BG;
        SetRect(_previewPanel, PREVIEW_X, contentY, PREVIEW_W, sepHeight);

        // Thumbnail image (centered in preview, with padding)
        int thumbPad = 16;
        int thumbW = PREVIEW_W - thumbPad * 2;
        int thumbH = (int)(sepHeight) - 80 - thumbPad * 2; // leave room for name text below
        var thumbGo = MakeChild(_previewPanel.transform, "Thumb");
        _thumbnailImage = thumbGo.AddComponent<RawImage>();
        _thumbnailImage.color = Color.white;
        SetRect(thumbGo, thumbPad, -thumbPad, thumbW, thumbH);
        _thumbnailImage.enabled = false;

        // Play icon overlay (large ▶ centered in preview, for animation folders)
        _previewPlayIcon = MakeText(_previewPanel.transform, "PlayIcon", "\u25B6",
            120, COL_ANIM, 0, -thumbPad, PREVIEW_W, thumbH, TextAnchor.MiddleCenter);
        _previewPlayIcon.enabled = false;

        // File/folder name text below thumbnail
        float nameY = -(thumbPad + thumbH + 8);
        _previewNameText = MakeText(_previewPanel.transform, "PreviewName", "",
            FONT_ENTRY, COL_FILE, thumbPad, nameY, thumbW, 60, TextAnchor.UpperCenter);
        _previewNameText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _previewNameText.verticalOverflow = VerticalWrapMode.Truncate;

        // Hint bar at bottom (full width)
        float hintY = contentY - sepHeight - 4;
        string vr   = "[L/R Stick] Navigate    [Trigger/A] Select    [B] Back    [Y] Close";
        string desk  = "[Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close";
        _hintText = MakeText(bg.transform, "Hint", XRSettings.isDeviceActive ? vr : desk,
            FONT_HINT, COL_HINT, PAD, hintY, CW - PAD * 2, HINT_H, TextAnchor.MiddleCenter);

        // Help panel (to the right of the main panel)
        var helpPanel = MakeChild(_root.transform, "HelpPanel");
        var helpBg = helpPanel.AddComponent<Image>();
        helpBg.color = new Color(0.05f, 0.05f, 0.07f, 1f);
        SetRect(helpPanel, CW + 24, -PAD, 420, 520);

        _helpText = MakeText(helpPanel.transform, "Help", "", FONT_HINT, Color.white,
            16, -16, 388, 488, TextAnchor.UpperLeft);
        _helpText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _helpText.verticalOverflow = VerticalWrapMode.Overflow;
        UpdateHelpText();
    }

    // ── UI Helpers ────────────────────────────────────────────────────────────

    static GameObject MakeChild(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void SetRect(GameObject go, float x, float y, float w, float h)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot     = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeText(Transform parent, string name, string text,
        int fontSize, Color color, float x, float y, float w, float h,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var go = MakeChild(parent, name);
        SetRect(go, x, y, w, h);
        var t = go.AddComponent<Text>();
        t.text      = text;
        t.fontSize  = fontSize;
        t.color     = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow   = VerticalWrapMode.Truncate;
        if (_font != null) t.font = _font;
        return t;
    }

    // ── UI Update ─────────────────────────────────────────────────────────────

    void UpdatePath()
    {
        _pathText.text = string.IsNullOrEmpty(_currentPath)
            ? "Computer (Drives)"
            : TruncatePath(_currentPath, 90);
        _pathText.color = COL_PATH; // reset color after error
    }

    void UpdateFpsDisplay()
    {
        if (_fpsText == null)
            return;

        float currentFps = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
        if (_smoothedFps <= 0f)
            _smoothedFps = currentFps;
        else
            _smoothedFps = Mathf.Lerp(_smoothedFps, currentFps, 0.1f);

        // Only update the UI text every ~15 frames to avoid per-frame string allocation
        if (Time.frameCount % 15 == 0)
            _fpsText.text = $"FPS {_smoothedFps:F0}";
    }

    bool IsCurrentSplatEntry(Entry entry)
    {
        return !entry.isDir
            && cycler != null
            && !string.IsNullOrEmpty(cycler.CurrentFileName)
            && string.Equals(entry.name, cycler.CurrentFileName, StringComparison.OrdinalIgnoreCase);
    }

    bool IsCurrentFavorite(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && !string.IsNullOrWhiteSpace(_currentPath)
            && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);
    }

    static string FormatFavoriteLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName))
            folderName = path;

        return folderName;
    }

    void UpdateRows()
    {
        for (int i = 0; i < ROWS; i++)
        {
            int idx = _favoriteScroll + i;
            if (idx < _favoriteFolders.Count)
            {
                string favoritePath = _favoriteFolders[idx];
                bool isCurrentFavorite = IsCurrentFavorite(favoritePath);
                _favoriteTexts[i].text = (isCurrentFavorite ? "★ " : "★ ") + FormatFavoriteLabel(favoritePath);
                _favoriteTexts[i].color = isCurrentFavorite ? COL_CURRENT : COL_FAVORITE;

                if (_activePane == BrowserPane.Favorites && idx == _favoriteSel)
                    _favoriteBgs[i].color = COL_SEL;
                else if (isCurrentFavorite)
                    _favoriteBgs[i].color = COL_CURRENT_BG;
                else if (i % 2 == 1)
                    _favoriteBgs[i].color = COL_FAVORITE_BG;
                else
                    _favoriteBgs[i].color = COL_CLEAR;
            }
            else if (_favoriteFolders.Count == 0 && i == 0)
            {
                _favoriteTexts[i].text = "(no favorites)";
                _favoriteTexts[i].color = COL_HINT;
                _favoriteBgs[i].color = _activePane == BrowserPane.Favorites ? COL_SEL : COL_CLEAR;
            }
            else
            {
                _favoriteTexts[i].text = "";
                _favoriteBgs[i].color = COL_CLEAR;
            }
        }

        for (int i = 0; i < ROWS; i++)
        {
            int idx = _scroll + i;
            if (idx < _entries.Count)
            {
                var e = _entries[idx];
                bool isCurrent = IsCurrentSplatEntry(e);
                string prefix;
                Color color;
                if (e.isAnimation)
                {
                    prefix = "\u25B6 "; // ▶ play triangle for animation folders
                    color = COL_ANIM;
                }
                else if (e.isDir)
                {
                    prefix = "\u25B8 "; // ▸ small triangle for regular folders
                    color = COL_DIR;
                }
                else if (isCurrent)
                {
                    prefix = "\u2605 "; // ★ star for currently loaded file
                    color = COL_CURRENT;
                }
                else
                {
                    prefix = "  ";
                    color = COL_FILE;
                }

                _rowTexts[i].text  = prefix + e.name;
                _rowTexts[i].color = color;

                if (_activePane == BrowserPane.Files && idx == _sel)
                    _rowBgs[i].color = COL_SEL;
                else if (isCurrent)
                    _rowBgs[i].color = COL_CURRENT_BG;
                else if (i % 2 == 1)
                    _rowBgs[i].color = COL_ROW_ALT;
                else
                    _rowBgs[i].color = COL_CLEAR;
            }
            else
            {
                _rowTexts[i].text  = "";
                _rowBgs[i].color   = COL_CLEAR;
            }
        }

        // Update hint with item count
        int dirs  = _entries.Count(e => e.isDir) - (string.IsNullOrEmpty(_currentPath) ? 0 : 1);
        int files = _entries.Count(e => !e.isDir);
        int anims = _entries.Count(e => e.isAnimation);
        string countInfo = $"{dirs} folder(s), {files} file(s)";
        if (anims > 0) countInfo += $", {anims} anim";
        if (_entries.Count > 0)
            countInfo += $"   [{_sel + 1}/{_entries.Count}]";

        string favoritesInfo = _favoriteFolders.Count > 0
            ? $"Favorites: {_favoriteFolders.Count}"
            : "Favorites: none";

        string paneInfo = _activePane == BrowserPane.Favorites ? "Pane: Favorites" : "Pane: Files";

        string controls = XRSettings.isDeviceActive
            ? "[Stick] Navigate    [Trigger or A] Select    [B] Back    [Y] Close    [L-Stick Click] Favorite +/-"
            : "[WASD/Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close    [F] Favorite +/-";

        string movieInfo = "";
        if (_movieState == MovieState.Playing && cycler != null)
            movieInfo = $"   |   Movie: {cycler.movieFps} FPS";
        else if (_movieState == MovieState.Loading)
        {
            float pct = _movieTotalCount > 0 ? (float)_movieLoadedCount / _movieTotalCount * 100f : 0f;
            movieInfo = $"   |   Movie: Loading {_movieLoadedCount}/{_movieTotalCount} ({pct:F0}%)";
        }

        _hintText.text = $"{countInfo}   |   {favoritesInfo}   |   {paneInfo}\n{controls}{movieInfo}";
        UpdateHelpText();
        UpdatePreview();
    }

    void UpdateHelpText()
    {
        if (_helpText == null) return;

        bool preloadOn = cycler != null && cycler.preloadUpcomingFiles;
        string preloadStatus = preloadOn
            ? $"Preload: ON ({cycler.preloadRamFraction:P0} RAM)"
            : "Preload: OFF";

        // Movie status line
        string movieStatus;
        if (_movieState == MovieState.Loading)
        {
            float pct = _movieTotalCount > 0 ? (float)_movieLoadedCount / _movieTotalCount * 100f : 0f;
            movieStatus = $"Movie: LOADING {_movieLoadedCount}/{_movieTotalCount} ({pct:F0}%)";
        }
        else if (_movieState == MovieState.Playing)
        {
            int fps = cycler != null ? cycler.movieFps : 0;
            int frames = cycler != null && cycler.loader != null ? cycler.loader.MovieFrameCount : 0;
            movieStatus = $"Movie: PLAYING {fps} FPS ({frames} frames)";
        }
        else
        {
            movieStatus = "Movie: OFF";
        }

        _helpText.text = XRSettings.isDeviceActive
            ? "Browser\n"
            + "Y: open / close\n"
            + "Stick: browse / switch pane\n"
            + "Trigger / A: open / load\n"
            + "B: parent folder\n"
            + "L-Stick click: add/remove favorite\n"
            + "X: toggle preload\n"
            + "Y: start movie\n\n"
            + "Locomotion\n"
            + "R-Stick Y: forward / back\n"
            + "R-Stick X: snap turn\n"
            + "L-Stick X: strafe\n"
            + "L-Stick Y: up / down\n\n"
            + "World Manipulation\n"
            + "R-Grip: grab & move world\n"
            + "R-Trigger: rotate world\n\n"
            + "Movie Playback\n"
            + "L-Stick left/right: FPS -/+\n"
            + "Y: stop movie\n\n"
            + "Scene\n"
            + "L-Grip + R-Stick: rotate splat\n"
            + "Both grips + move hands: scale splat\n"
            + "L-Grip + X: flip\n"
            + "L-Grip + A: reset rotation\n\n"
            + preloadStatus + "\n"
            + movieStatus
            : "Browser\n"
            + "Esc / Tab: open / close\n"
            + "WASD / Arrows: browse / switch pane\n"
            + "Enter: open / load\n"
            + "Backspace: parent folder\n"
            + "F: add/remove favorite\n"
            + "P: toggle preload\n"
            + "M: start / stop movie\n\n"
            + "Movie Playback\n"
            + "Left / Right: FPS -/+\n"
            + "M: stop movie\n\n"
            + "Scene\n"
            + "Mouse: look / drag    WASD: move\n"
            + "Shift: sprint    Space / C: up / down\n"
            + "R / F: next / previous splat\n"
            + "Q / E: rotate splat\n"
            + "Home: reset    End: flip\n\n"
            + preloadStatus + "\n"
            + movieStatus;
    }

    void UpdatePreview()
    {
        if (_thumbnailImage == null) return;

        // Default: hide everything
        _thumbnailImage.enabled = false;
        _previewPlayIcon.enabled = false;
        _previewNameText.text = "";

        if (_sel < 0 || _sel >= _entries.Count)
            return;

        var entry = _entries[_sel];

        // Animation folder: show ▶ icon + folder name + frame count
        if (entry.isAnimation)
        {
            _previewPlayIcon.enabled = true;
            _previewNameText.text = $"{entry.name}\n{entry.animFrameCount} frames";
            _previewNameText.color = COL_ANIM;

            // Check for a folder thumbnail (folder_name.jpg in parent)
            if (TryLoadThumbnail(entry.path, isFolder: true))
                _previewPlayIcon.enabled = false; // hide icon if we have a real image
            return;
        }

        // Regular folder: show folder name
        if (entry.isDir)
        {
            _previewNameText.text = entry.name;
            _previewNameText.color = COL_DIR;
            return;
        }

        // Splat file: try to load matching thumbnail
        _previewNameText.color = COL_FILE;
        if (TryLoadThumbnail(entry.path, isFolder: false))
        {
            // Thumbnail loaded — show filename below
            _previewNameText.text = entry.name;
        }
        else
        {
            // No thumbnail — show filename as placeholder
            _previewNameText.text = entry.name;
            _previewNameText.color = COL_HINT;
        }
    }

    /// <summary>Try to load a thumbnail for the given path. Returns true if loaded.</summary>
    bool TryLoadThumbnail(string itemPath, bool isFolder)
    {
        string baseName = isFolder ? Path.GetFileName(itemPath) : Path.GetFileNameWithoutExtension(itemPath);
        string dir = isFolder ? Path.GetDirectoryName(itemPath) : Path.GetDirectoryName(itemPath);
        if (string.IsNullOrEmpty(dir)) return false;

        // Look for matching image: .jpg, .jpeg, .png (case-insensitive via both cases)
        string imgPath = null;
        string[] exts = { ".jpg", ".jpeg", ".png", ".JPG", ".JPEG", ".PNG" };
        foreach (string ext in exts)
        {
            string candidate = Path.Combine(dir, baseName + ext);
            if (File.Exists(candidate))
            {
                imgPath = candidate;
                break;
            }
        }

        if (imgPath == null)
            return false;

        // Skip re-loading if it's the same image
        if (imgPath == _lastThumbnailPath && _thumbnailImage.texture != null)
        {
            _thumbnailImage.enabled = true;
            return true;
        }

        try
        {
            byte[] data = File.ReadAllBytes(imgPath);
            if (_thumbnailTex == null)
                _thumbnailTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (_thumbnailTex.LoadImage(data))
            {
                _thumbnailImage.texture = _thumbnailTex;
                _thumbnailImage.enabled = true;
                _lastThumbnailPath = imgPath;
                return true;
            }
        }
        catch { /* ignore read errors */ }

        return false;
    }

    void EnsureVisible()
    {
        if (_sel < _scroll)           _scroll = _sel;
        if (_sel >= _scroll + ROWS)   _scroll = _sel - ROWS + 1;
    }

    void EnsureFavoriteVisible()
    {
        if (_favoriteSel < _favoriteScroll) _favoriteScroll = _favoriteSel;
        if (_favoriteSel >= _favoriteScroll + ROWS) _favoriteScroll = _favoriteSel - ROWS + 1;
    }

    static string TruncatePath(string p, int maxLen)
    {
        if (p.Length <= maxLen) return p;
        return "..." + p.Substring(p.Length - maxLen + 3);
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 pos = cam.transform.position + fwd * spawnDistance;
        _root.transform.position = pos;
        // Canvas front faces toward the user
        _root.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── XR Input Helpers ──────────────────────────────────────────────────────

    static readonly List<InputDevice> s_devices = new(2);

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static bool ReadStickClick(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool v))
            return v;
        return false;
    }

    static float ReadNavigationY()
    {
        float leftY = ReadStickY(XRNode.LeftHand);
        float rightY = ReadStickY(XRNode.RightHand);
        return Mathf.Abs(leftY) >= Mathf.Abs(rightY) ? leftY : rightY;
    }

    static float ReadNavigationX()
    {
        float leftX = ReadStickX(XRNode.LeftHand);
        float rightX = ReadStickX(XRNode.RightHand);
        return Mathf.Abs(leftX) >= Mathf.Abs(rightX) ? leftX : rightX;
    }

    static float ReadStickX(XRNode node)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.x;
        return 0f;
    }

    static float ReadStickY(XRNode node)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.y;
        return 0f;
    }

    static bool ReadTrigger(XRNode node)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(CommonUsages.trigger, out float v))
            return v > 0.5f;
        return false;
    }
}
