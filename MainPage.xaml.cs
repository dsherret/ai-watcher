namespace AIWatcher;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;

    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.StartPolling(Dispatcher);
    }

    protected override void OnDisappearing()
    {
        _vm.StopPolling();
        base.OnDisappearing();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is AIInstance instance)
        {
            _vm.ActivateCommand.Execute(instance);

            // clear selection so the same item can be tapped again
            if (sender is CollectionView cv)
                cv.SelectedItem = null;
        }
    }
}
