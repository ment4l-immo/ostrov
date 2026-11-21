using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>
/// Связывает мост с хуком и остров: показывает запросы Claude карточками по очереди.
/// Если остров не виден (трей или полноэкранное приложение) или ты сейчас в VS Code (М-014), запросы сразу отпускаются обычному окну VS Code:
/// отвечать на острове незачем, когда окно с запросом перед глазами. Уже показанную карточку фокус в VS Code не убирает.
/// </summary>
internal sealed class ClaudePrompts : IDisposable
{
    private const string DenyMessage = "Запрещено пользователем на острове";

    private readonly Dispatcher _ui;
    private readonly ClaudeBridge _bridge = new();
    private readonly List<ClaudeRequest> _waiting = [];
    private ClaudeRequest? _current;
    private IPromptCard? _card;

    public ClaudePrompts(Dispatcher ui) => _ui = ui;

    /// <summary>Событие начала или конца работы Claude (приходит на потоке интерфейса).</summary>
    public event Action<ClaudeEvent>? EventReceived;

    public void Start()
    {
        _bridge.RequestReceived += r => _ui.BeginInvoke(() => OnRequest(r));
        _bridge.EventReceived += e => _ui.BeginInvoke(() => EventReceived?.Invoke(e));
        IslandBus.VisibilityChanged += OnVisibilityChanged;
        _bridge.Start();
    }

    public void Dispose()
    {
        IslandBus.VisibilityChanged -= OnVisibilityChanged;
        _bridge.Dispose();
    }

    private void OnRequest(ClaudeRequest request)
    {
        if (!IslandBus.IsVisible || ActivityApps.IsVsCode(IslandBus.ForegroundApp))
        {
            request.Release();
            return;
        }

        request.Closed += () => _ui.BeginInvoke(() => OnClosed(request));
        if (request.IsClosed) return; // хук отключился до того, как мы успели показать

        // Ответили в окне VS Code: в файле переписки появляется результат этого вызова. Тогда отпускаем хук и убираем карточку.
        if (request.TranscriptPath is { Length: > 0 } transcript)
        {
            var watcher = new TranscriptWatcher(transcript, request.Tool, request.Input, () => _ui.BeginInvoke(() =>
            {
                request.Release();
                OnClosed(request);
            }));
            request.Finished += watcher.Dispose;
            if (request.IsFinished) watcher.Dispose();
        }

        if (_current == null)
        {
            Show(request);
        }
        else
        {
            _waiting.Add(request);
            _card?.SetQueued(_waiting.Count);
        }
    }

    private void Show(ClaudeRequest request)
    {
        _current = request;

        if (request.Tool == "AskUserQuestion")
        {
            // Вопросы Claude: выбор вариантов на острове, ответ уходит хуком (updatedInput.answers).
            var question = QuestionCard.TryCreate(request,
                answers => { if (request == _current) { request.AllowWithAnswers(answers); Next(); } },
                () => { if (request == _current) { request.Release(); Next(); } }); // «В VS Code»
            if (question == null)
            {
                // Ввод не похож на вопросы: отдаём окну VS Code, не гадаем.
                request.Release();
                Next();
                return;
            }
            _card = question;
            question.SetQueued(_waiting.Count);
            // Экран вопроса сменился: подгоняем высоту острова (карточка и размер остаются теми же).
            question.ScreenChanged += () => { if (request == _current) IslandBus.ShowOverlay(question, QuestionCard.CardWidth, question.PreferredHeight); };
            IslandBus.ShowOverlay(question, QuestionCard.CardWidth, question.PreferredHeight);
            return;
        }

        var card = new PermissionCard(request, () => Answer(request, allow: true), () => Answer(request, allow: false));
        _card = card;
        card.SetQueued(_waiting.Count);
        IslandBus.ShowOverlay(card, PermissionCard.CardWidth, PermissionCard.CardHeight);
    }

    private void Answer(ClaudeRequest request, bool allow)
    {
        if (request != _current) return;
        if (allow) request.Allow();
        else request.Deny(DenyMessage);
        Next();
    }

    private void Next()
    {
        _current = null;
        _card = null;
        if (_waiting.Count > 0)
        {
            var next = _waiting[0];
            _waiting.RemoveAt(0);
            Show(next);
        }
        else
        {
            IslandBus.ClearOverlay();
        }
    }

    // Запрос закрыт со стороны хука (например, ответили в окне VS Code): убираем карточку.
    private void OnClosed(ClaudeRequest request)
    {
        if (request == _current) Next();
        else if (_waiting.Remove(request)) _card?.SetQueued(_waiting.Count);
    }

    // Остров спрятался (трей или полноэкранное приложение): ответить на острове нельзя, всё отдаём окну VS Code.
    private void OnVisibilityChanged(bool visible)
    {
        if (visible) return;
        _current?.Release();
        foreach (var r in _waiting) r.Release();
        _waiting.Clear();
        _current = null;
        _card = null;
        IslandBus.ClearOverlay();
    }
}

/// <summary>Карточка на острове, которой можно показать, сколько ещё запросов ждёт очереди.</summary>
internal interface IPromptCard
{
    void SetQueued(int count);
}
