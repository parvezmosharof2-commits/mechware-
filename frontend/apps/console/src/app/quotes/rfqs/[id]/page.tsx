import { RfqDetailView } from '../../../../components/RfqDetailView';

export default async function RfqDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <RfqDetailView id={id} />;
}
