using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanvasDesktop;

internal static class ZoneTheme
{
    internal static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static void Apply(Window window)
    {
        // Scope the Fluent theme to this window; existing WinForms UI stays independent.
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        // WPF still marks per-window theme selection experimental in .NET 10.
#pragma warning disable WPF0001
        window.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        window.Resources.Add(typeof(System.Windows.Controls.Button), System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Background" Value="#24324A"/>
              <Setter Property="Foreground" Value="#F1F5F9"/>
              <Setter Property="BorderBrush" Value="#475569"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Frame" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="7" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.8"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#93C5FD"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.45"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """));
        var textStyle = new Style(typeof(TextBox));
        textStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#1A2538")));
        textStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#F1F5F9")));
        textStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brush("#475569")));
        textStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        window.Resources.Add(typeof(TextBox), textStyle);
        window.Resources.Add(typeof(CheckBox), System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <Setter Property="Foreground" Value="#F1F5F9"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="CheckBox">
                    <DockPanel LastChildFill="True">
                      <Border x:Name="Box" Width="20" Height="20" Margin="0,0,12,0"
                              VerticalAlignment="Center" Background="#1A2538" BorderBrush="#64748B"
                              BorderThickness="1" CornerRadius="4">
                        <TextBlock x:Name="Tick" Text="✓" Foreground="White" Visibility="Collapsed"
                                   FontSize="14" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ContentPresenter VerticalAlignment="Center"/>
                    </DockPanel>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Tick" Property="Visibility" Value="Visible"/>
                        <Setter TargetName="Box" Property="Background" Value="#2563EB"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#93C5FD"/></Trigger>
                      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """));
        window.Background = Brush("#111827"); window.Foreground = Brushes.WhiteSmoke;
        window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 14;
    }

    public static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }
}
