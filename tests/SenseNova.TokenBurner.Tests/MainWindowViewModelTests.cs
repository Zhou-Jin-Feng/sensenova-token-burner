using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Desktop.ViewModels;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    public void SelectingPresetUpdatesTargetAndNotifiesBoundLabels()
    {
        var viewModel = new MainWindowViewModel();
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.SelectPresetCommand.Execute("50");

        Assert.AreEqual(50, viewModel.Percentage);
        Assert.AreEqual(60_000_000L, viewModel.TargetTokens);
        Assert.AreEqual("50%", viewModel.PercentageLabel);
        CollectionAssert.Contains(notifications, nameof(viewModel.TargetTokensLabel));
        CollectionAssert.Contains(notifications, nameof(viewModel.SelectionHint));
    }

    [TestMethod]
    public void SelectingZeroProducesNoRunTargetAndClearExplanation()
    {
        var viewModel = new MainWindowViewModel { Percentage = 0 };

        Assert.AreEqual(0L, viewModel.TargetTokens);
        StringAssert.Contains(viewModel.SelectionHint, "不发送");
    }

    [TestMethod]
    public void InvalidSelectionDoesNotReplaceValidSelection()
    {
        var viewModel = new MainWindowViewModel { Percentage = 50 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => viewModel.Percentage = 100);

        Assert.AreEqual(50, viewModel.Percentage);
        Assert.AreEqual(60_000_000L, viewModel.TargetTokens);
        Assert.IsFalse(viewModel.SelectPresetCommand.CanExecute("100"));
        viewModel.SelectPresetCommand.Execute("100");
        Assert.AreEqual(50, viewModel.Percentage);
    }
}
