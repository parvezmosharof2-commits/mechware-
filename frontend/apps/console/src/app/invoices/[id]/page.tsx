import { InvoiceDetailView } from '../../../components/InvoiceDetailView';

export default async function InvoiceDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <InvoiceDetailView id={id} />;
}
