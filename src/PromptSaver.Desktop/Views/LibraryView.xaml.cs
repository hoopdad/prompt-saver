using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PromptSaver.Application.Dtos;
using PromptSaver.Desktop.ViewModels;

namespace PromptSaver.Desktop.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplySortDirection();
    }

    private async void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel ||
            !Enum.TryParse(e.Column.SortMemberPath, out PromptSortColumn column))
        {
            return;
        }

        e.Handled = true;
        await viewModel.SortAsync(column);
        ApplySortDirection();
    }

    private void ApplySortDirection()
    {
        if (DataContext is not LibraryViewModel viewModel)
        {
            return;
        }

        foreach (DataGridColumn column in LibraryResults.Columns)
        {
            column.SortDirection =
                Enum.TryParse(column.SortMemberPath, out PromptSortColumn columnKind) &&
                columnKind == viewModel.SortColumn
                    ? viewModel.SortDirection == PromptSortDirection.Ascending
                        ? ListSortDirection.Ascending
                        : ListSortDirection.Descending
                    : null;
        }
    }
}
