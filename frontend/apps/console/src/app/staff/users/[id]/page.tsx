import { StaffUserDetailView } from '../../../../components/StaffUserDetailView';

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <StaffUserDetailView id={id} />;
}
