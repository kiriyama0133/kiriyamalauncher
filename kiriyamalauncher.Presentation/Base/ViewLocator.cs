using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;

namespace kiriyamalauncher.Presentation.Base;

/// <summary>
/// Provides a data template for resolving and instantiating views based on their corresponding view models
/// in an MVVM pattern for Avalonia applications.
/// </summary>
public class ViewLocator : IDataTemplate
{
    /// <summary>
    /// Builds a view <see cref="Control"/> for the specified view model data object.
    /// </summary>
    /// <param name="data">The view model instance for which to create a view.</param>
    /// <returns>
    /// The corresponding view <see cref="Control"/> if found; otherwise, a <see cref="TextBlock"/> indicating the view was not found.
    /// </returns>
    public Control? Build(object? data)
    {
        if (data is null)
        {
            return null;
        }

        string name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        Type? type = data.GetType().Assembly.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            control.DataContext = data;
            return control;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    /// <summary>
    /// Determines whether this template matches the specified data object.
    /// </summary>
    /// <param name="data">The data object to check.</param>
    /// <returns>
    /// <c>true</c> if the data object is a <see cref="BaseViewModel"/>; otherwise, <c>false</c>.
    /// </returns>
    public bool Match(object? data)
    {
        return data is BaseViewModel;
    }
}
