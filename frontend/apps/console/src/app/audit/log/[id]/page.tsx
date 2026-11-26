import { AuditLogDetailView } from '../../../../components/AuditLogDetailView';

export default async function AuditLogDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <AuditLogDetailView id={id} />;
}
