import { ShippingZoneEditorView } from '../../../../components/ShippingZoneEditorView';

export default async function ShippingZonePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <ShippingZoneEditorView id={id} />;
}
