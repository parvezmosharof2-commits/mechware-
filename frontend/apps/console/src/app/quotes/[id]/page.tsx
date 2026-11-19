import { QuoteDetailView } from '../../../components/QuoteDetailView';

export default async function QuoteDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <QuoteDetailView id={id} />;
}
