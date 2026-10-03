using CommunityToolkit.Maui.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;

namespace Vitals.Maui.Views;

/// <summary>
/// Keeps toolkit popups comfortable in portrait while allowing them to use
/// the available landscape viewport when a phone is rotated.
/// </summary>
internal static class ResponsivePopupSizing
{
    private const double PortraitHorizontalMargin = 24;
    private const double PortraitVerticalMargin = 24;
    private const double LandscapeHorizontalMargin = 32;
    private const double LandscapeVerticalMargin = 24;
    private const double MaxLandscapeWidth = 960;
    private const double MinLandscapeHeight = 240;

    public static void Attach(
        Popup popup,
        VisualElement content,
        double portraitWidth,
        double portraitHeight)
    {
        EventHandler<DisplayInfoChangedEventArgs>? displayChanged = null;
        EventHandler? opened = null;
        EventHandler? closed = null;

        void ApplySize()
        {
            var info = DeviceDisplay.Current.MainDisplayInfo;
            var density = info.Density > 0 ? info.Density : 1.0;
            var displayWidth = info.Width / density;
            var displayHeight = info.Height / density;
            var isLandscape =
                info.Orientation == DisplayOrientation.Landscape ||
                displayWidth > displayHeight;

            if (isLandscape)
            {
                content.WidthRequest = Math.Min(
                    MaxLandscapeWidth,
                    Math.Max(portraitWidth, displayWidth - LandscapeHorizontalMargin));

                content.HeightRequest = Math.Max(
                    MinLandscapeHeight,
                    displayHeight - LandscapeVerticalMargin);
            }
            else
            {
                content.WidthRequest = Math.Min(
                    portraitWidth,
                    Math.Max(0, displayWidth - PortraitHorizontalMargin));

                content.HeightRequest = Math.Min(
                    portraitHeight,
                    Math.Max(0, displayHeight - PortraitVerticalMargin));
            }
        }

        void QueueApplySize() =>
            MainThread.BeginInvokeOnMainThread(ApplySize);

        displayChanged = (_, _) => QueueApplySize();
        opened = (_, _) => QueueApplySize();
        closed = (_, _) =>
        {
            DeviceDisplay.Current.MainDisplayInfoChanged -= displayChanged;
            popup.Opened -= opened;
            popup.Closed -= closed;
        };

        DeviceDisplay.Current.MainDisplayInfoChanged += displayChanged;
        popup.Opened += opened;
        popup.Closed += closed;

        QueueApplySize();
    }
}
