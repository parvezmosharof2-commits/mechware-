import { WithholdingDetailView } from '../../../../components/WithholdingDetailView';

export default async function WithholdingDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <WithholdingDetailView id={id} />;
}
