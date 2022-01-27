﻿using LibVLCSharp.Platforms.UWP;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp.UI;
using Microsoft.UI.Xaml.Controls;
using ModernVLC.Converters;
using ModernVLC.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Media;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace ModernVLC.ViewModels
{
    internal partial class PlayerViewModel : ObservableObject, IDisposable
    {
        public ICommand PlayPauseCommand { get; private set; }
        public ICommand SeekCommand { get; private set; }
        public ICommand SetTimeCommand { get; private set; }
        public ICommand FullscreenCommand { get; private set; }
        public ICommand SetAudioTrackCommand { get; private set; }
        public ICommand SetSubtitleCommand { get; private set; }
        public ICommand AddSubtitleCommand { get; private set; }
        public ICommand SetPlaybackSpeedCommand { get; private set; }
        public ICommand ChangeVolumeCommand { get; private set; }
        public ICommand OpenCommand { get; private set; }
        public ICommand ToggleControlsVisibilityCommand { get; private set; }
        public ICommand ToggleCompactLayoutCommand { get; private set; }

        public PlayerService MediaPlayer
        {
            get => _mediaPlayer;
            set => SetProperty(ref _mediaPlayer, value);
        }

        public string MediaTitle
        {
            get => _mediaTitle;
            set => SetProperty(ref _mediaTitle, value);
        }

        public bool IsFullscreen
        {
            get => _isFullscreen;
            private set => SetProperty(ref _isFullscreen, value);
        }

        public bool IsCompact
        {
            get => _isCompact;
            private set => SetProperty(ref _isCompact, value);
        }

        public bool BufferingVisible
        {
            get => _bufferingVisible;
            private set => SetProperty(ref _bufferingVisible, value);
        }

        public bool ControlsHidden
        {
            get => _controlsHidden;
            private set => SetProperty(ref _controlsHidden, value);
        }

        public bool ZoomToFit
        {
            get => _zoomToFit;
            set
            {
                if (SetProperty(ref _zoomToFit, value))
                {
                    OnSizeChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public Control VideoView { get; set; }
        public object ToBeOpened { get; set; }

        private LibVLC LibVLC => App.DerivedCurrent.LibVLC;

        private readonly DispatcherQueue DispatcherQueue;
        private readonly DispatcherQueueTimer ControlsVisibilityTimer;
        private readonly DispatcherQueueTimer StatusMessageTimer;
        private readonly DispatcherQueueTimer BufferingTimer;
        private readonly SystemMediaTransportControls TransportControl;
        private Media _media;
        private string _mediaTitle;
        private PlayerService _mediaPlayer;
        private bool _isFullscreen;
        private bool _controlsHidden;
        private CoreCursor _cursor;
        private bool _pointerMovedOverride;
        private bool _isCompact;
        private string _statusMessage;
        private bool _zoomToFit;
        private bool _bufferingVisible;

        public PlayerViewModel()
        {
            DispatcherQueue = DispatcherQueue.GetForCurrentThread();
            TransportControl = SystemMediaTransportControls.GetForCurrentView();
            ControlsVisibilityTimer = DispatcherQueue.CreateTimer();
            StatusMessageTimer = DispatcherQueue.CreateTimer();
            BufferingTimer = DispatcherQueue.CreateTimer();
            PlayPauseCommand = new RelayCommand(PlayPause);
            SeekCommand = new RelayCommand<long>(Seek, (long _) => MediaPlayer.IsSeekable);
            SetTimeCommand = new RelayCommand<RangeBaseValueChangedEventArgs>(SetTime);
            ChangeVolumeCommand = new RelayCommand<double>(ChangeVolume);
            FullscreenCommand = new RelayCommand<bool>(SetFullscreen);
            SetAudioTrackCommand = new RelayCommand<int>(SetAudioTrack);
            SetSubtitleCommand = new RelayCommand<int>(SetSubtitle);
            SetPlaybackSpeedCommand = new RelayCommand<float>(SetPlaybackSpeed);
            OpenCommand = new RelayCommand<object>(Open);
            ToggleControlsVisibilityCommand = new RelayCommand(ToggleControlsVisibility);
            ToggleCompactLayoutCommand = new RelayCommand(ToggleCompactLayout);

            MediaDevice.DefaultAudioRenderDeviceChanged += MediaDevice_DefaultAudioRenderDeviceChanged;
            TransportControl.ButtonPressed += TransportControl_ButtonPressed;
            InitSystemTransportControls();
        }

        private void ChangeVolume(double changeAmount)
        {
            MediaPlayer.ObservableVolume += changeAmount;
            ShowStatusMessage($"Volume {MediaPlayer.ObservableVolume:F0}%");
        }

        private async void ToggleCompactLayout()
        {
            var view = ApplicationView.GetForCurrentView();
            if (IsCompact)
            {
                if (await view.TryEnterViewModeAsync(ApplicationViewMode.Default))
                {
                    IsCompact = false;
                }
            }
            else
            {
                var preferences = ViewModePreferences.CreateDefault(ApplicationViewMode.CompactOverlay);
                preferences.ViewSizePreference = ViewSizePreference.Custom;
                preferences.CustomSize = new Size(240 * (MediaPlayer.NumericAspectRatio ?? 1), 240);
                if (await view.TryEnterViewModeAsync(ApplicationViewMode.CompactOverlay, preferences))
                {
                    IsCompact = true;
                }
            }
        }

        private async void Open(object value)
        {
            Media media = null;
            Uri uri = null;

            if (value is StorageFile file)
            {
                var extension = file.FileType.ToLowerInvariant();
                if (!file.IsAvailable || !FileService.SupportedFormats.Contains(extension)) return;
                var stream = await file.OpenStreamForReadAsync();
                media = new Media(LibVLC, new StreamMediaInput(stream));
                uri = new Uri(file.Path);
            }

            if (value is Uri)
            {
                uri = (Uri)value;
                media = new Media(LibVLC, uri);
            }

            if (media == null || uri == null) return;

            MediaTitle = uri.Segments.LastOrDefault();
            var oldMedia = _media;
            _media = media;
            media.ParsedChanged += OnMediaParsed;
            MediaPlayer.Play(media);
            oldMedia?.Dispose();
        }

        private void OnMediaParsed(object sender, MediaParsedChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var dimension = MediaPlayer.Dimension;
                var view = ApplicationView.GetForCurrentView();
                if (view.VisibleBounds.Width >= dimension.Width ||
                    view.VisibleBounds.Height >= dimension.Height) return;

                // Try some scaler to reach as close to 1.0 as possible.
                // Due to UWP limitation, setting 1.0 size won't always work.
                if (SetWindowSize(1.0)) return;
                if (SetWindowSize(0.99)) return;
                if (SetWindowSize(0.94)) return;
            });
        }

        private void SetPlaybackSpeed(float speed)
        {
            if (speed != MediaPlayer.Rate)
            {
                MediaPlayer.SetRate(speed);
            }
        }

        private void SetSubtitle(int index)
        {
            if (MediaPlayer.Spu != index)
            {
                MediaPlayer.SetSpu(index);
            }
        }

        private void SetAudioTrack(int index)
        {
            if (MediaPlayer.AudioTrack != index)
            {
                MediaPlayer.SetAudioTrack(index);
            }
        }

        private void MediaDevice_DefaultAudioRenderDeviceChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
        {
            if (args.Role == AudioDeviceRole.Default)
            {
                MediaPlayer.SetOutputDevice(MediaPlayer.OutputDevice);
            }
        }

        private void SetFullscreen(bool value)
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.IsFullScreenMode && !value)
            {
                view.ExitFullScreenMode();
            }

            if (!view.IsFullScreenMode && value)
            {
                view.TryEnterFullScreenMode();
            }

            IsFullscreen = view.IsFullScreenMode;
        }

        public void Initialize(object sender, InitializedEventArgs e)
        {
            var libVlc = App.DerivedCurrent.LibVLC;
            if (libVlc == null)
            {
                App.DerivedCurrent.LibVLC = libVlc = new LibVLC(enableDebugLogs: true, e.SwapChainOptions);
            }
            
            MediaPlayer = new PlayerService(libVlc);
            MediaPlayer.PropertyChanged += MediaPlayer_PropertyChanged;
            RegisterMediaPlayerPlaybackEvents();

            ConfigureVideoViewManipulation();
            if (ToBeOpened != null) Open(ToBeOpened);
        }

        public void ShowStatusMessage(string message)
        {
            StatusMessage = message;
            StatusMessageTimer.Debounce(() => StatusMessage = null, TimeSpan.FromSeconds(1));
        }

        private void MediaPlayer_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayerService.ObservableState))
            {
                if (ControlsHidden && MediaPlayer.ObservableState != VLCState.Playing)
                {
                    ShowControls();
                }

                if (!ControlsHidden && MediaPlayer.ObservableState == VLCState.Playing)
                {
                    DelayHideControls();
                }
            }

            if (e.PropertyName == nameof(PlayerService.BufferingProgress))
            {
                BufferingTimer.Debounce(
                    () => BufferingVisible = MediaPlayer.BufferingProgress < 100,
                    TimeSpan.FromSeconds(0.5));
            }
        }

        public void Dispose()
        {
            _media?.Dispose();
            MediaPlayer.Dispose();
            TransportControl.PlaybackStatus = MediaPlaybackStatus.Closed;
        }

        private void PlayPause()
        {
            if (MediaPlayer.IsPlaying && MediaPlayer.CanPause)
            {
                MediaPlayer.Pause();
            }

            if (!MediaPlayer.IsPlaying && MediaPlayer.WillPlay)
            {
                MediaPlayer.Play();
            }

            if (MediaPlayer.State == VLCState.Ended)
            {
                MediaPlayer.Replay();
            }
        }

        private void Seek(long amount)
        {
            if (MediaPlayer.IsSeekable)
            {
                MediaPlayer.Time += amount;
                ShowStatusMessage($"{HumanizedDurationConverter.Convert(MediaPlayer.Time)} / {HumanizedDurationConverter.Convert(MediaPlayer.Length)}");
            }
        }

        public void SetInteracting(bool interacting)
        {
            MediaPlayer.ShouldUpdateTime = !interacting;
        }

        public bool JumpFrame(bool previous = false)
        {
            if (MediaPlayer.State == VLCState.Paused && MediaPlayer.IsSeekable)
            {
                if (previous)
                {
                    MediaPlayer.Time -= MediaPlayer.FrameDuration;
                }
                else
                {
                    MediaPlayer.NextFrame();
                }

                return true;
            }

            return false;
        }

        public void ToggleControlsVisibility()
        {
            if (ControlsHidden)
            {
                ShowControls();
            }
            else if (MediaPlayer.IsPlaying)
            {
                HideControls();
            }
        }

        public bool SetWindowSize(double scaler)
        {
            if (scaler <= 0) return false;
            var displayInformation = DisplayInformation.GetForCurrentView();
            var maxWidth = displayInformation.ScreenWidthInRawPixels;
            var view = ApplicationView.GetForCurrentView();
            var videoDimension = MediaPlayer.Dimension;
            if (!videoDimension.IsEmpty)
            {
                var aspectRatio = videoDimension.Width / videoDimension.Height;
                var newWidth = videoDimension.Width * scaler;
                if (newWidth > maxWidth) newWidth = maxWidth;
                var newHeight = newWidth / aspectRatio;
                scaler = newWidth / videoDimension.Width;
                if (view.TryResizeView(new Size(newWidth, newHeight)))
                {
                    ShowStatusMessage($"Scale {scaler * scaler * 100:0.##}%");
                    return true;
                }
            }

            return false;
        }

        public void FocusVideoView()
        {
            VideoView.Focus(FocusState.Programmatic);
        }

        public void OnSizeChanged()
        {
            if (MediaPlayer == null) return;
            if (!ZoomToFit && MediaPlayer.CropGeometry == null) return;
            MediaPlayer.CropGeometry = ZoomToFit ? $"{VideoView.ActualWidth}:{VideoView.ActualHeight}" : null;
        }

        public void OnPointerMoved()
        {
            if (!_pointerMovedOverride)
            {
                if (ControlsHidden)
                {
                    ShowCursor();
                    ControlsHidden = false;
                }

                if (!MediaPlayer.ShouldUpdateTime) return;
                DelayHideControls();
            }
        }

        public void OnDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Open";
        }

        public async void OnDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0)
                {
                    Open(items[0]);
                }
            }

            if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                if (uri.IsFile)
                {
                    Open(uri);
                }
            }
        }

        public void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(VideoView);
            var mouseWheelDelta = pointer.Properties.MouseWheelDelta;
            ChangeVolume(mouseWheelDelta / 25.0);
        }

        public void OnProcessKeyboardAccelerators(object sender, ProcessKeyboardAcceleratorEventArgs args)
        {
            long seekAmount = 0;
            int volumeChange = 0;
            int direction = 0;

            switch (args.Key)
            {
                case VirtualKey.Left:
                    direction = -1;
                    break;
                case VirtualKey.Right:
                    direction = 1;
                    break;
                case VirtualKey.Up:
                    volumeChange = 10;
                    break;
                case VirtualKey.Down:
                    volumeChange = -10;
                    break;
                case VirtualKey.Number1:
                    SetWindowSize(0.25);
                    return;
                case VirtualKey.Number2:
                    SetWindowSize(0.5);
                    return;
                case VirtualKey.Number3:
                    SetWindowSize(0.75);
                    return;
                case VirtualKey.Number4:
                    SetWindowSize(1);
                    return;
                case VirtualKey.Number5:
                    SetWindowSize(1.25);
                    return;
                case VirtualKey.Number6:
                    SetWindowSize(1.5);
                    return;
                case VirtualKey.Number7:
                    SetWindowSize(1.75);
                    return;
                case VirtualKey.Number8:
                    SetWindowSize(2);
                    return;
                case VirtualKey.Number9:
                    SetWindowSize(4);
                    return;
                case (VirtualKey)190 when args.Modifiers == VirtualKeyModifiers.None:   // Period (".")
                    JumpFrame(false);
                    return;
                case (VirtualKey)188 when args.Modifiers == VirtualKeyModifiers.None:   // Comma (",")
                    JumpFrame(true);
                    return;
            }

            switch (args.Modifiers)
            {
                case VirtualKeyModifiers.Control:
                    seekAmount = 10000;
                    break;
                case VirtualKeyModifiers.Shift:
                    seekAmount = 1000;
                    break;
                case VirtualKeyModifiers.None:
                    seekAmount = 5000;
                    break;
            }

            if (seekAmount * direction != 0)
            {
                Seek(seekAmount * direction);
            }

            if (volumeChange != 0)
            {
                ChangeVolume(volumeChange);
            }
        }

        public void AudioTrack_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (args.AddedItems[0] == null) return;
            var selected = (TrackDescription)args.AddedItems[0];
            SetAudioTrack(selected.Id);
        }

        public void Subtitles_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (args.AddedItems[0] == null) return;
            var selected = (TrackDescription)args.AddedItems[0];
            SetSubtitle(selected.Id);
        }

        private void ShowControls()
        {
            ShowCursor();
            ControlsHidden = false;
            DelayHideControls();
        }

        private void HideControls()
        {
            ControlsHidden = true;
            HideCursor();
        }

        private void DelayHideControls()
        {
            ControlsVisibilityTimer.Debounce(() =>
            {
                if (MediaPlayer.IsPlaying && VideoView.FocusState != FocusState.Unfocused)
                {
                    HideCursor();
                    ControlsHidden = true;

                    // Workaround for PointerMoved is raised when show/hide cursor
                    _pointerMovedOverride = true;
                    Task.Delay(1000).ContinueWith(t => _pointerMovedOverride = false);
                }
            }, TimeSpan.FromSeconds(5));
        }

        private void HideCursor()
        {
            var coreWindow = Window.Current.CoreWindow;
            if (coreWindow.PointerCursor?.Type == CoreCursorType.Arrow)
            {
                _cursor = coreWindow.PointerCursor;
                coreWindow.PointerCursor = null;
            }
        }

        private void ShowCursor()
        {
            var coreWindow = Window.Current.CoreWindow;
            if (coreWindow.PointerCursor == null)
            {
                coreWindow.PointerCursor = _cursor;
            }
        }

        private void SetTime(RangeBaseValueChangedEventArgs args)
        {
            if (MediaPlayer.IsSeekable)
            {
                if ((args.OldValue == MediaPlayer.Time || !MediaPlayer.IsPlaying) ||
                    !MediaPlayer.ShouldUpdateTime &&
                    args.NewValue != MediaPlayer.Length)
                {
                    if (MediaPlayer.State == VLCState.Ended)
                    {
                        MediaPlayer.Replay();
                    }

                    MediaPlayer.Time = (long)args.NewValue;
                }
            }
        }
    }
}
