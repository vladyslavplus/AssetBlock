import type { Metadata } from 'next'
import { ModerationCaseWorkspace } from '@/components/moderation/moderation-case-workspace'

export const dynamic = 'force-dynamic'

export const metadata: Metadata = {
  title: 'Moderation case · AssetBlock',
  description: 'Review a submitted asset version.',
}

export default async function ModerationCasePage({
  params,
}: {
  params: Promise<{ submissionId: string }>
}) {
  const { submissionId } = await params
  return <ModerationCaseWorkspace submissionId={submissionId} />
}
