using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Services;
using Windows.UI;

namespace Veil.Windows;

public sealed partial class SettingsWindow
{
    private PowerPlanPreset? _newProfilePlan;
    private int? _newProfileCpu;
    private int? _newProfileHz;
    private bool? _newProfileTransparency;
    private bool? _newProfileAnimations;
    private string? _editingProfileId;

    private void RebuildProfileCards()
    {
        var store = WindowsProfileStore.Current;

        BuiltInProfileCardsPanel.Children.Clear();
        foreach (var profile in store.Profiles.Where(p => p.IsBuiltIn))
            BuiltInProfileCardsPanel.Children.Add(BuildProfileCard(profile, store.ActiveProfileId));

        UserProfileCardsPanel.Children.Clear();
        var userProfiles = store.Profiles.Where(p => !p.IsBuiltIn).ToList();
        UserProfilesLabel.Visibility = userProfiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var profile in userProfiles)
            UserProfileCardsPanel.Children.Add(BuildProfileCard(profile, store.ActiveProfileId));
    }

    private UIElement BuildProfileCard(WindowsProfile profile, string? activeProfileId)
    {
        bool isActive = profile.Id == activeProfileId;

        var nameBlock = new TextBlock
        {
            Text = profile.Name,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var summaryBlock = new TextBlock
        {
            Text = profile.BuildSummary(),
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            TextWrapping = TextWrapping.WrapWholeWords
        };

        var leftStack = new StackPanel { Spacing = 3 };
        if (isActive)
        {
            var activeBadge = new TextBlock
            {
                Text = "● Active",
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = new SolidColorBrush(Color.FromArgb(255, 74, 222, 128))
            };
            leftStack.Children.Add(activeBadge);
        }
        leftStack.Children.Add(nameBlock);
        leftStack.Children.Add(summaryBlock);

        var applyButton = new Button
        {
            Content = isActive ? "Applied" : "Apply",
            Width = 72,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            FontSize = 12,
            IsTabStop = false,
            Tag = profile.Id
        };
        applyButton.Background = new SolidColorBrush(Color.FromArgb(isActive ? (byte)48 : (byte)20, 255, 255, 255));
        applyButton.Foreground = new SolidColorBrush(Color.FromArgb(isActive ? (byte)255 : (byte)214, 255, 255, 255));
        applyButton.Click += OnApplyProfileClick;

        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightStack.Children.Add(applyButton);

        var editButton = new Button
        {
            Content = "Edit",
            Width = 54,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            FontSize = 12,
            IsTabStop = false,
            Tag = profile.Id
        };
        editButton.Click += OnEditProfileClick;
        rightStack.Children.Add(editButton);

        if (profile.IsBuiltIn)
        {
            var resetButton = new Button
            {
                Content = "Reset",
                Width = 58,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
                Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                FontSize = 12,
                IsTabStop = false,
                Tag = profile.Id
            };
            resetButton.Click += OnResetBuiltInProfileClick;
            rightStack.Children.Add(resetButton);
        }

        if (!profile.IsBuiltIn)
        {
            var deleteButton = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
                Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                FontSize = 12,
                IsTabStop = false,
                Tag = profile.Id
            };
            deleteButton.Click += OnDeleteProfileClick;
            rightStack.Children.Add(deleteButton);
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(rightStack, 1);
        row.Children.Add(leftStack);
        row.Children.Add(rightStack);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Child = row
        };
    }

    private async void OnApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var store = WindowsProfileStore.Current;
        var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile == null) return;

        if (sender is Button btn)
        {
            btn.Content = "…";
            btn.IsEnabled = false;
        }

        try
        {
            if (store.ActiveProfileId is null)
            {
                store.SaveBaseProfile(await WindowsProfileService.CaptureCurrentAsync());
            }
            await WindowsProfileService.ApplyAsync(profile);
            store.SetActiveProfile(id);
            RebuildProfileCards();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to apply profile.", ex);
            if (sender is Button b)
            {
                b.Content = "Error";
                b.IsEnabled = true;
            }
        }
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        WindowsProfileStore.Current.RemoveUserProfile(id);
        RebuildProfileCards();
    }

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var profile = WindowsProfileStore.Current.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        _editingProfileId = id;
        _newProfilePlan = profile.PowerPlan;
        _newProfileCpu = profile.CpuMaxPercent;
        _newProfileHz = profile.RefreshRateHz;
        _newProfileTransparency = profile.TransparencyEnabled;
        _newProfileAnimations = profile.AnimationsEnabled;

        NewProfileNameTextBox.Text = profile.Name;
        SaveNewProfileButton.Content = profile.IsBuiltIn ? "Save Preset" : "Save Profile";
        NewProfileFormBorder.Visibility = Visibility.Visible;
        AddProfileButton.Visibility = Visibility.Collapsed;
        SyncNewProfileFormButtons();
    }

    private void OnResetBuiltInProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        WindowsProfileStore.Current.ResetBuiltInProfile(id);
        if (_editingProfileId == id)
        {
            CloseProfileForm();
        }
        RebuildProfileCards();
    }

    private async void OnRestoreWindowsDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        btn.Content = "…";
        btn.IsEnabled = false;

        try
        {
            await WindowsProfileService.RestoreWindowsDefaultsAsync();
            WindowsProfileStore.Current.SetActiveProfile(null);
            RebuildProfileCards();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to restore Windows previous profile.", ex);
        }
        finally
        {
            btn.Content = "Restore";
            btn.IsEnabled = true;
        }
    }

    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        _editingProfileId = null;
        _newProfilePlan = null;
        _newProfileCpu = null;
        _newProfileHz = null;
        _newProfileTransparency = null;
        _newProfileAnimations = null;

        NewProfileNameTextBox.Text = string.Empty;
        SaveNewProfileButton.Content = "Create Profile";
        NewProfileFormBorder.Visibility = Visibility.Visible;
        AddProfileButton.Visibility = Visibility.Collapsed;

        SyncNewProfileFormButtons();
    }

    private void OnCancelNewProfileClick(object sender, RoutedEventArgs e)
    {
        CloseProfileForm();
    }

    private void OnSaveNewProfileClick(object sender, RoutedEventArgs e)
    {
        var name = NewProfileNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = "My Profile";

        var profile = new WindowsProfile
        {
            Name = name,
            PowerPlan = _newProfilePlan,
            CpuMaxPercent = _newProfileCpu,
            RefreshRateHz = _newProfileHz,
            TransparencyEnabled = _newProfileTransparency,
            AnimationsEnabled = _newProfileAnimations
        };

        var store = WindowsProfileStore.Current;
        if (_editingProfileId is not null)
        {
            profile.Id = _editingProfileId;
            store.UpdateProfile(profile);
        }
        else
        {
            store.AddUserProfile(profile);
        }

        CloseProfileForm();
        RebuildProfileCards();
    }

    private void CloseProfileForm()
    {
        _editingProfileId = null;
        NewProfileFormBorder.Visibility = Visibility.Collapsed;
        AddProfileButton.Visibility = Visibility.Visible;
        SaveNewProfileButton.Content = "Create Profile";
    }

    private void OnNewProfilePlanClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _newProfilePlan = string.IsNullOrEmpty(tag) ? null
            : Enum.TryParse<PowerPlanPreset>(tag, out var plan) ? plan : null;
        SyncNewProfileFormButtons();
    }

    private void OnNewProfileCpuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _newProfileCpu = string.IsNullOrEmpty(tag) ? null
            : int.TryParse(tag, out int v) ? v : null;
        SyncNewProfileFormButtons();
    }

    private void OnNewProfileHzClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _newProfileHz = string.IsNullOrEmpty(tag) ? null
            : int.TryParse(tag, out int v) ? v : null;
        SyncNewProfileFormButtons();
    }

    private void OnNewProfileTransparencyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _newProfileTransparency = tag == "on" ? true : tag == "off" ? false : null;
        SyncNewProfileFormButtons();
    }

    private void OnNewProfileAnimationsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _newProfileAnimations = tag == "on" ? true : tag == "off" ? false : null;
        SyncNewProfileFormButtons();
    }

    private void SyncNewProfileFormButtons()
    {
        SyncFormToggle(NewProfilePlanBalancedButton, _newProfilePlan == PowerPlanPreset.Balanced);
        SyncFormToggle(NewProfilePlanPerformanceButton, _newProfilePlan == PowerPlanPreset.HighPerformance);
        SyncFormToggle(NewProfilePlanSaverButton, _newProfilePlan == PowerPlanPreset.PowerSaver);
        SyncFormToggle(NewProfilePlanEfficiencyButton, _newProfilePlan == PowerPlanPreset.BestEfficiency);
        SyncFormToggle(NewProfilePlanUltimateButton, _newProfilePlan == PowerPlanPreset.UltimatePerformance);
        SyncFormToggle(NewProfilePlanNoneButton, _newProfilePlan == null);

        SyncFormToggle(NewProfileCpu60Button, _newProfileCpu == 60);
        SyncFormToggle(NewProfileCpu75Button, _newProfileCpu == 75);
        SyncFormToggle(NewProfileCpu90Button, _newProfileCpu == 90);
        SyncFormToggle(NewProfileCpu100Button, _newProfileCpu == 100);
        SyncFormToggle(NewProfileCpuNoneButton, _newProfileCpu == null);

        SyncFormToggle(NewProfileHz60Button, _newProfileHz == 60);
        SyncFormToggle(NewProfileHz75Button, _newProfileHz == 75);
        SyncFormToggle(NewProfileHz120Button, _newProfileHz == 120);
        SyncFormToggle(NewProfileHz144Button, _newProfileHz == 144);
        SyncFormToggle(NewProfileHzNoneButton, _newProfileHz == null);

        SyncFormToggle(NewProfileTransparencyOnButton, _newProfileTransparency == true);
        SyncFormToggle(NewProfileTransparencyOffButton, _newProfileTransparency == false);
        SyncFormToggle(NewProfileTransparencyKeepButton, _newProfileTransparency == null);

        SyncFormToggle(NewProfileAnimationsOnButton, _newProfileAnimations == true);
        SyncFormToggle(NewProfileAnimationsOffButton, _newProfileAnimations == false);
        SyncFormToggle(NewProfileAnimationsKeepButton, _newProfileAnimations == null);
    }

    private static void SyncFormToggle(Button button, bool isSelected)
    {
        button.Background = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)48 : (byte)0, 255, 255, 255));
        button.Foreground = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)255 : (byte)180, 255, 255, 255));
    }
}
