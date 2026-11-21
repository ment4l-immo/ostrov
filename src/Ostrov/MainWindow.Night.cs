using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov;

// Ночной режим (решение пользователя 2026-11-14): с 22:00 до 07:00 остров приглушается: цветные фоны слабее, белый текст и значки мягче (≈ 80 %).
// Включается и выключается плавно (2,5 с); время фиксированное, выключатель в «Настройки → Общие».
public partial class MainWindow
{
    private const int NightFromHour = 22, NightToHour = 7;
    private readonly DispatcherTimer _nightTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    // 0 — день, 1 — ночь.
    public static readonly DependencyProperty NightAmountProperty = DependencyProperty.Register(
        nameof(NightAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double NightAmount
    {
        get => (double)GetValue(NightAmountProperty);
        set => SetValue(NightAmountProperty, value);
    }

    /// <summary>Во сколько раз темнее белое содержимое ночью (1 — как днём).</summary>
    private double NightInk => 1 - 0.2 * NightAmount;

    /// <summary>Во сколько раз слабее цветные фоны ночью.</summary>
    private double NightGlow => 1 - 0.35 * NightAmount;

    internal static bool IsNight(DateTime now, bool enabled) => enabled && (now.Hour >= NightFromHour || now.Hour < NightToHour);

    private void InitNight()
    {
        UpdateNight(animate: false);
        _nightTimer.Tick += (_, _) => UpdateNight(animate: true);
        _nightTimer.Start();
        IslandBus.SettingsChanged += () => Dispatcher.BeginInvoke(() => UpdateNight(animate: true));
    }

    private void UpdateNight(bool animate)
    {
        var target = IsNight(DateTime.Now, AppSettings.Load().NightMode) ? 1.0 : 0.0;
        if (Math.Abs(NightAmount - target) < 0.001 && !HasAnimatedProperties) return;
        if (!animate)
        {
            BeginAnimation(NightAmountProperty, null);
            NightAmount = target;
            return;
        }
        BeginAnimation(NightAmountProperty, new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(2500)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
