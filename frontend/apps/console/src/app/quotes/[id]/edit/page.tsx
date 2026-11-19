import Link from 'next/link';
import { ConsoleShell } from '../../../../components/ConsoleShell';

/** Placeholder: the draft editor is D4.16 and send/approve are D4.17. Nothing here changes data. */
export default async function QuoteEditPlaceholderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return (
    <ConsoleShell activeItemId="quotes-list" title="Quote editor">
      <p role="status" data-testid="editor-not-built" className="rounded-md border border-surface-border bg-surface p-4 text-sm text-text-muted">
        The editor for draft lines, prices and terms is not built yet (unit D4.16), and neither are sending and approval (D4.17).
        Until then a quote can be read, revised and closed.
      </p>
      <Link href={`/quotes/${id}`} className="text-sm font-semibold text-brand-accent underline">Back to the quote</Link>
    </ConsoleShell>
  );
}
