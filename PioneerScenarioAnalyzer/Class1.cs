using System.Text.Json;
using System.Text.Json.Serialization;
using Gallop;
using Gallop.Endpoints;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace PioneerScenarioAnalyzer;

public sealed class PioneerScenarioAnalyzer : IPlugin
{
    const string WorkspaceTitle = "PioneerScenarioAnalyzer";
    const string TrainingPanelKey = "training";
    const string SettingsFileName = "settings.json";
    const int DefaultHistoryLimit = 100;
    const int MaximumHistoryLimit = 1000;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    readonly object gate = new();
    readonly List<HistoryEntry> history = [];

    IApplication? application;
    Workspace? workspace;
    HistoryView? historyView;
    WorkspaceContent? historyPanelContent;
    WorkspaceContent? liveContent;
    WorkspaceContent? displayedContent;
    int historyLimit = DefaultHistoryLimit;
    int selectedIndex = -1;
    long displayVersion;
    long generation;
    bool hasUnread;
    bool hasPublishedTrainingPanel;
    bool initialized;

    string DataDirectory => Path.Combine("PluginData", WorkspaceTitle);
    string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = LoadSettings();

        context.Analyzers.Register<SingleModePioneerCheckEventResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_pioneer/check_event")],
            invocation => Analyze(invocation.Payload),
            priority: 1);

        lock (gate)
        {
            if (initialized)
                throw new InvalidOperationException("PioneerScenarioAnalyzer 已初始化。");

            application = context.Application;
            historyLimit = settings.HistoryLimit;
            history.Clear();
            liveContent = null;
            displayedContent = null;
            selectedIndex = -1;
            displayVersion = 0;
            generation++;
            hasUnread = false;
            hasPublishedTrainingPanel = false;
            initialized = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        HistoryView? view;
        Workspace? publishedWorkspace;
        bool removePanel;

        lock (gate)
        {
            initialized = false;
            generation++;
            history.Clear();
            liveContent = null;
            displayedContent = null;
            selectedIndex = -1;
            hasUnread = false;

            view = historyView;
            publishedWorkspace = workspace;
            removePanel = hasPublishedTrainingPanel;

            historyView = null;
            historyPanelContent = null;
            workspace = null;
            application = null;
            hasPublishedTrainingPanel = false;
        }

        view?.Stop();
        if (removePanel)
            publishedWorkspace!.RemovePanel(TrainingPanelKey);
        return ValueTask.CompletedTask;
    }

    public async Task ConfigPromptAsync(
        IApplication application,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        cancellationToken.ThrowIfCancellationRequested();
        if (application.TopRunnable is null &&
            Environment.CurrentManagedThreadId != application.MainThreadId)
        {
            throw new InvalidOperationException(
                "PioneerScenarioAnalyzer 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");
        }

        var draft = LoadSettings();
        HistorySettings saved;
        if (Environment.CurrentManagedThreadId == application.MainThreadId)
        {
            saved = RunConfigDialog(application, draft, cancellationToken);
        }
        else
        {
            var completion = new TaskCompletionSource<HistorySettings>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            application.Invoke(() =>
            {
                try
                {
                    completion.SetResult(RunConfigDialog(application, draft, cancellationToken));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            saved = await completion.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SaveSettings(saved);
        ApplyHistoryLimit(saved.HistoryLimit);
    }

    public ValueTask Analyze(SingleModePioneerCheckEventResponse response)
    {
        var data = response.data;
        if (data.chara_info.scenario_id != 11)
            return ValueTask.CompletedTask;
        if (data.chara_info.state is 2 or 3)
            return ValueTask.CompletedTask;
        if (data.home_info?.command_info_array is null)
            return ValueTask.CompletedTask;
        if (data.unchecked_event_array is { Length: > 0 } || data.race_start_info is not null)
            return ValueTask.CompletedTask;

        var stage = Handler.GetCommandInfoStage(response);
        if (stage == 0)
            return ValueTask.CompletedTask;

        long currentGeneration;
        lock (gate)
        {
            if (!initialized)
                return ValueTask.CompletedTask;
            currentGeneration = generation;
        }

        var key = new HistoryKey(data.chara_info.single_mode_chara_id, data.chara_info.turn);
        var content = Handler.ParsePioneerCommandInfo(response, stage);
        PublishTrainingPanel(currentGeneration, key, content);
        return ValueTask.CompletedTask;
    }

    void PublishTrainingPanel(long currentGeneration, HistoryKey key, WorkspaceContent content)
    {
        lock (gate)
        {
            if (!initialized || generation != currentGeneration)
                return;

            EnsureHistoryPanelLocked();
            var firstPublish = !hasPublishedTrainingPanel;
            workspace!.SetPanel(
                TrainingPanelKey,
                "训练分析",
                historyPanelContent!,
                fullBleed: true,
                switchToWorkspace: firstPublish);
            hasPublishedTrainingPanel = true;
            liveContent = content;

            if (historyLimit == 0)
            {
                history.Clear();
                selectedIndex = -1;
                hasUnread = false;
                DisplayLocked(content, switchToWorkspace: false);
                return;
            }

            var existingIndex = history.FindIndex(entry => entry.Key == key);
            if (existingIndex >= 0)
            {
                history[existingIndex] = new(key, content);
                if (selectedIndex == existingIndex)
                    DisplayLocked(content, switchToWorkspace: false);
                return;
            }

            var selectedKey = selectedIndex >= 0 ? history[selectedIndex].Key : (HistoryKey?)null;
            var followNewest = selectedKey is null || selectedIndex == history.Count - 1;
            history.Add(new(key, content));
            TrimOldestLocked();

            if (followNewest)
            {
                selectedIndex = history.Count - 1;
                hasUnread = false;
                DisplayLocked(history[selectedIndex].Content, switchToWorkspace: false);
                return;
            }

            var preservedKey = selectedKey.GetValueOrDefault();
            selectedIndex = history.FindIndex(entry => entry.Key == preservedKey);
            if (selectedIndex < 0)
            {
                selectedIndex = history.Count - 1;
                hasUnread = false;
                DisplayLocked(history[selectedIndex].Content, switchToWorkspace: false);
                return;
            }

            if (!hasUnread)
            {
                hasUnread = true;
                workspace!.Notify("有新的训练分析；按 → 查看最新。", UiSeverity.Info);
            }
        }
    }

    void EnsureHistoryPanelLocked()
    {
        workspace ??= Workspace.Create(WorkspaceTitle);
        if (historyPanelContent is null)
        {
            var capturedApplication = application
                ?? throw new InvalidOperationException("PioneerScenarioAnalyzer 尚未初始化。");
            var capturedWorkspace = workspace;
            var capturedGeneration = generation;
            historyPanelContent = new(
                () => CreateHistoryView(
                    capturedApplication,
                    capturedWorkspace,
                    capturedGeneration));
        }
    }

    View CreateHistoryView(
        IApplication capturedApplication,
        Workspace capturedWorkspace,
        long capturedGeneration)
    {
        lock (gate)
        {
            if (!initialized || generation != capturedGeneration)
            {
                return new View
                {
                    Width = Dim.Fill(),
                    Height = Dim.Auto()
                };
            }

            var view = new HistoryView(capturedApplication, capturedWorkspace, NavigateHistory);
            historyView = view;
            if (displayedContent is not null)
                view.ShowInitial(displayedContent, displayVersion);
            return view;
        }
    }

    void DisplayLocked(WorkspaceContent content, bool switchToWorkspace)
    {
        var version = ++displayVersion;
        displayedContent = content;
        var publishedWorkspace = workspace!;
        var panelContent = historyPanelContent!;
        var currentGeneration = generation;
        void RefreshPanel()
        {
            lock (gate)
            {
                if (!initialized || generation != currentGeneration)
                    return;
                publishedWorkspace.SetPanel(
                    TrainingPanelKey,
                    "训练分析",
                    panelContent,
                    fullBleed: true,
                    switchToWorkspace: switchToWorkspace);
            }
        }
        if (historyView is { } view)
            view.Show(content, version, RefreshPanel);
        else
            RefreshPanel();
        hasPublishedTrainingPanel = true;
    }

    bool NavigateHistory(HistoryNavigation navigation)
    {
        lock (gate)
        {
            if (!initialized || historyLimit == 0 || history.Count == 0)
                return false;

            var next = navigation switch
            {
                HistoryNavigation.Older => Math.Max(0, selectedIndex - 1),
                HistoryNavigation.Newer => Math.Min(history.Count - 1, selectedIndex + 1),
                HistoryNavigation.Oldest => 0,
                HistoryNavigation.Newest => history.Count - 1,
                _ => throw new ArgumentOutOfRangeException(nameof(navigation))
            };

            if (next != selectedIndex)
            {
                selectedIndex = next;
                DisplayLocked(history[selectedIndex].Content, switchToWorkspace: false);
            }

            if (selectedIndex == history.Count - 1)
                hasUnread = false;
            workspace!.Notify($"历史 {selectedIndex + 1}/{history.Count}", UiSeverity.Info);
            return true;
        }
    }

    void ApplyHistoryLimit(int value)
    {
        ValidateHistoryLimit(value);
        lock (gate)
        {
            historyLimit = value;
            if (value == 0)
            {
                history.Clear();
                selectedIndex = -1;
                hasUnread = false;
                if (historyView is not null && liveContent is not null)
                    DisplayLocked(liveContent, switchToWorkspace: false);
                return;
            }

            if (history.Count <= value)
                return;

            var selectedKey = selectedIndex >= 0 ? history[selectedIndex].Key : (HistoryKey?)null;
            TrimOldestLocked();
            selectedIndex = selectedKey is null
                ? -1
                : history.FindIndex(entry => entry.Key == selectedKey.Value);
            if (selectedIndex >= 0)
                return;

            selectedIndex = history.Count - 1;
            hasUnread = false;
            if (selectedIndex >= 0 && historyView is not null)
                DisplayLocked(history[selectedIndex].Content, switchToWorkspace: false);
        }
    }

    void TrimOldestLocked()
    {
        var overflow = history.Count - historyLimit;
        if (overflow > 0)
            history.RemoveRange(0, overflow);
    }

    HistorySettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
            return new() { HistoryLimit = DefaultHistoryLimit };

        try
        {
            var settings = JsonSerializer.Deserialize<HistorySettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions)
                ?? throw new JsonException("根值不能为 null。");
            ValidateHistoryLimit(settings.HistoryLimit);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"无法读取 {SettingsPath}：{ex.Message}", ex);
        }
    }

    void SaveSettings(HistorySettings settings)
    {
        ValidateHistoryLimit(settings.HistoryLimit);
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    static void ValidateHistoryLimit(int value)
    {
        if (value is < 0 or > MaximumHistoryLimit)
        {
            throw new InvalidDataException(
                $"historyLimit 必须在 0 到 {MaximumHistoryLimit} 之间，当前值为 {value}。");
        }
    }

    static HistorySettings RunConfigDialog(
        IApplication application,
        HistorySettings draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var dialog = new Dialog
        {
            Title = "PioneerScenarioAnalyzer 配置",
            Width = 58,
            Height = 10
        };
        dialog.Add(new Label
        {
            X = 1,
            Y = 1,
            Text = $"History 上限（0-{MaximumHistoryLimit}，0 为禁用）"
        });
        var limit = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = draft.HistoryLimit.ToString()
        };
        var validation = new Label
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = 1
        };
        dialog.Add(limit, validation);

        HistorySettings? result = null;
        var save = new Button { Text = "保存", IsDefault = true };
        save.Accepting += (_, e) =>
        {
            if (!int.TryParse(limit.Text, out var value) || value is < 0 or > MaximumHistoryLimit)
            {
                validation.Text = $"History 上限必须是 0 到 {MaximumHistoryLimit} 之间的整数。";
                e.Handled = true;
                return;
            }

            result = new() { HistoryLimit = value };
            application.RequestStop(dialog);
            e.Handled = true;
        };
        var cancel = new Button { Text = "取消" };
        cancel.Accepting += (_, e) =>
        {
            application.RequestStop(dialog);
            e.Handled = true;
        };
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        limit.SetFocus();

        using (cancellationToken.Register(
                   () => application.Invoke(() => application.RequestStop(dialog))))
        {
            application.Run(dialog);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result
            ?? throw new OperationCanceledException(
                "PioneerScenarioAnalyzer 配置已取消。",
                cancellationToken);
    }

    readonly record struct HistoryKey(int SingleModeCharaId, int Turn);
    sealed record HistoryEntry(HistoryKey Key, WorkspaceContent Content);

    sealed class HistorySettings
    {
        [JsonRequired]
        public int HistoryLimit { get; init; }
    }

    enum HistoryNavigation
    {
        Older,
        Newer,
        Oldest,
        Newest
    }

    sealed class HistoryView : View
    {
        readonly object contentGate = new();
        readonly IApplication application;
        readonly Workspace workspace;
        readonly Func<HistoryNavigation, bool> navigate;

        View? currentContent;
        long requestedVersion;
        bool stopped;

        internal HistoryView(
            IApplication application,
            Workspace workspace,
            Func<HistoryNavigation, bool> navigate)
        {
            this.application = application;
            this.workspace = workspace;
            this.navigate = navigate;

            Width = Dim.Fill();
            Height = Dim.Auto();
            CanFocus = true;
            TabStop = TabBehavior.TabGroup;
            application.Keyboard.KeyDown += ApplicationKeyDown;
        }

        internal void ShowInitial(WorkspaceContent content, long version)
        {
            lock (contentGate)
                requestedVersion = version;
            ShowCore(content, version);
        }

        internal void Show(WorkspaceContent content, long version, Action afterShow)
        {
            lock (contentGate)
            {
                if (stopped || version <= requestedVersion)
                    return;
                requestedVersion = version;
            }
            application.Invoke(() =>
            {
                if (ShowCore(content, version))
                    afterShow();
            });
        }

        internal void Stop()
        {
            lock (contentGate)
            {
                if (stopped)
                    return;
                stopped = true;
            }
            application.Keyboard.KeyDown -= ApplicationKeyDown;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Stop();
            base.Dispose(disposing);
        }

        bool ShowCore(WorkspaceContent content, long version)
        {
            lock (contentGate)
            {
                if (stopped || version != requestedVersion)
                    return false;
            }

            var next = content.CreateView();
            next.X = 0;
            next.Y = 0;
            next.Width = Dim.Fill();

            var previous = currentContent;
            currentContent = next;
            previous?.SuperView?.Remove(previous);
            previous?.Dispose();
            Add(next);
            Height = next.Height is DimFill ? Dim.Fill() : Dim.Auto();
            SetNeedsLayout();
            SetNeedsDraw();
            return true;
        }

        void ApplicationKeyDown(object? sender, Key key)
        {
            if (key.Handled || key.IsCtrl || key.IsAlt || key.IsShift)
                return;
            if (!ReferenceEquals(Workspace.Current, workspace))
                return;

            var focused = application.TopRunnableView?.MostFocused;
            if (focused is null || !Contains(focused))
                return;

            var direction = key.KeyCode switch
            {
                var code when code == Key.CursorUp.KeyCode => HistoryNavigation.Older,
                var code when code == Key.CursorDown.KeyCode => HistoryNavigation.Newer,
                var code when code == Key.CursorLeft.KeyCode => HistoryNavigation.Oldest,
                var code when code == Key.CursorRight.KeyCode => HistoryNavigation.Newest,
                _ => (HistoryNavigation?)null
            };
            if (direction is null || !navigate(direction.Value))
                return;

            key.Handled = true;
        }

        bool Contains(View focused)
        {
            for (View? current = focused; current is not null; current = current.SuperView)
            {
                if (ReferenceEquals(current, this))
                    return true;
            }
            return false;
        }
    }
}
