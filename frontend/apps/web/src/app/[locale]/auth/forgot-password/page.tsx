import { AuthFlowSurface } from '../../../../components/auth/AuthModal';

interface ForgotPasswordPageProps {
  params: Promise<{ locale: string }>;
}

export default async function ForgotPasswordPage({ params }: ForgotPasswordPageProps) {
  const resolvedParams = await params;
  const locale = resolvedParams.locale === 'bn' ? 'bn' : 'en';

  return (
    <main className="flex min-h-screen flex-col items-center justify-center bg-bg-canvas p-6">
      <AuthFlowSurface locale={locale} initialMode="forgot" />
    </main>
  );
}
