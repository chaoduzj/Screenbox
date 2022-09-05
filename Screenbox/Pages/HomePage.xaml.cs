﻿using Windows.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Screenbox.ViewModels;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Screenbox.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class HomePage : ContentPage
    {
        internal HomePageViewModel ViewModel => (HomePageViewModel)DataContext;

        public HomePage()
        {
            this.InitializeComponent();
            DataContext = App.Services.GetRequiredService<HomePageViewModel>();
            VisualStateManager.GoToState(this, ViewModel.HasRecentMedia ? "RecentMedia" : "Welcome", false);
        }
    }
}
