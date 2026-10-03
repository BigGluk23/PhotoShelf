namespace PhotoShelf.Desktop;

public enum DuplicateSearchScope
{
    CurrentView,
    IncludedFolders,
    CurrentFolder,
    CompareTwoFolders,
    WholeLibrary
}

public enum DuplicateSearchMode
{
    Exact,
    Similar
}

public sealed record DuplicateSearchRequest(DuplicateSearchMode Mode, DuplicateSearchScope Scope);
