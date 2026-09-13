using kiriyamalauncher.Business.Modules.Sample.DomainServices;
using kiriyamalauncher.Business.Modules.Sample.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;
using static kiriyamalauncher.Business.Modules.Sample.DomainServices.LineSorter;

namespace kiriyamalauncher.Business.Modules.Sample.ApplicationServices;

public class SampleToolsService : ISampleToolsService
{
    private readonly FlatUIColorPicker _flatColorProvider;
    private readonly LineSorter _lineSorter;
    private readonly UUIDGenerator _guidGenerator;

    public SampleToolsService(FlatUIColorPicker flatColorProvider, LineSorter lineSorter, UUIDGenerator guidGenerator)
    {
        _flatColorProvider = flatColorProvider;
        _lineSorter = lineSorter;
        _guidGenerator = guidGenerator;
    }

    public IEnumerable<FlatColorDto> GetFlatColors()
    {
        return _flatColorProvider.GetFlatColors();
    }

    public async Task InitializeLineSortingAsync(SortTypes _selectedSortType, string? textToSort)
    {
        await _lineSorter.InitiateAsync(textToSort, _selectedSortType);
    }

    public async Task InitializeGUIDGenerationAsync(bool shouldCapitalize = true)
    {
        await _guidGenerator.InitiateAsync(shouldCapitalize);
    }
}
