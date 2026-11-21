using System.Windows.Controls;
using System.Windows.Threading;

namespace Ostrov.Search;

/// <summary>
/// Подсказка в пустой строке Alt+Q: первая строка («Спроси что угодно») стоит целиком, потом её по буквам стирают и по буквам печатают
/// следующий пример, держат пару секунд, стирают и так по кругу. Ничего не прыгает и не сдвигается, меняется только текст.
/// Работает, пока строка пуста и идёт первый вопрос; остальное (ключ, «Уточнить…», ошибки) остаётся обычным статичным текстом.
/// </summary>
internal sealed class PlaceholderTyper
{
    private enum Phase { Hold, Erase, Gap, Type }

    private const int TickMs = 24; // буква печатается через тик (≈ 21 в секунду), стирается каждый тик (≈ 42)
    private const int FirstHoldMs = 2800, HoldMs = 2100, GapMs = 220;

    private readonly TextBlock _target;
    private readonly IReadOnlyList<string> _lines;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    private Phase _phase;
    private int _index, _shown, _wait;
    private bool _typeSkip;

    public PlaceholderTyper(TextBlock target, IReadOnlyList<string> lines)
    {
        _target = target;
        _lines = lines;
        _timer.Tick += (_, _) => Tick();
    }

    public bool Running => _timer.IsEnabled;

    /// <summary>Начать с первой строки целиком.</summary>
    public void Start()
    {
        if (_lines.Count == 0) return;
        _index = 0;
        _shown = _lines[0].Length;
        _target.Text = _lines[0];
        _phase = Phase.Hold;
        _wait = FirstHoldMs / TickMs;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        switch (_phase)
        {
            case Phase.Hold:
                if (--_wait <= 0) _phase = Phase.Erase;
                break;
            case Phase.Erase:
                _shown = Math.Max(0, _shown - 1); // стираем вдвое быстрее, чем печатаем
                _target.Text = _lines[_index][.._shown];
                if (_shown == 0) { _phase = Phase.Gap; _wait = GapMs / TickMs; }
                break;
            case Phase.Gap:
                if (--_wait > 0) break;
                _index = (_index + 1) % _lines.Count;
                _phase = Phase.Type;
                break;
            case Phase.Type:
                _typeSkip = !_typeSkip;
                if (_typeSkip) break; // буква через тик
                var line = _lines[_index];
                _shown = Math.Min(line.Length, _shown + 1);
                _target.Text = line[.._shown];
                if (_shown == line.Length) { _phase = Phase.Hold; _wait = HoldMs / TickMs; }
                break;
        }
    }
}
