import { ReturnDetailView } from '../../../components/ReturnDetailView';

export default async function ReturnDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <ReturnDetailView id={id} />;
}
