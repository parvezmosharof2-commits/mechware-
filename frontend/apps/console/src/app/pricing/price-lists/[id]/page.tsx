import { PriceListEditorView } from '../../../../components/PriceListEditorView';

export default async function PriceListPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <PriceListEditorView id={id} />;
}
