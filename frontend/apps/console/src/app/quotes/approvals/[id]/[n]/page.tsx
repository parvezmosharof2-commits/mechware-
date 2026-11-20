import { QuoteApprovalDecisionView } from '../../../../../components/QuoteApprovalDecisionView';

export default async function QuoteApprovalDecisionPage({ params }: { params: Promise<{ id: string; n: string }> }) {
  const { id, n } = await params;
  return <QuoteApprovalDecisionView id={id} versionNo={Number.parseInt(n, 10)} />;
}
