import { QuoteEditorView } from '../../../../components/QuoteEditorView';

export default async function QuoteEditPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <QuoteEditorView id={id} />;
}
