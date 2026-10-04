using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace TuiLivePreview.VisualStudio;

/// <summary>
/// Forwards edits to open views, so a running preview follows the buffer rather than the last
/// save. <see cref="PreviewService.BufferChanged"/> decides whether an edit matters.
/// </summary>
[VisualStudioContribution]
internal sealed class LiveMirror(PreviewService service) : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener
{
    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromGlobPattern("**/*.tui.cs", relativePath: false)],
    };

    public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        ITextDocumentSnapshot document = args.AfterTextView.Document;
        if (document.Uri.IsFile)
        {
            service.BufferChanged(document.Uri.LocalPath, () => PreviewService.TextOf(document));
        }

        return Task.CompletedTask;
    }

    public Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        if (textView.Document.Uri.IsFile)
        {
            service.BufferClosed(textView.Document.Uri.LocalPath);
        }

        return Task.CompletedTask;
    }
}
