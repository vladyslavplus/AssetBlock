'use client'

import { Badge } from '@/components/ui/badge'
import type { AssetVersionSummaryApi } from '@/lib/catalog/assets-api'
import { formatBytes } from '@/lib/format-bytes'
import { formatShortMonthDate } from '@/lib/format-date'
import { ContentHashDisplay } from '@/components/assets/content-hash-display'

interface AssetVersionHistoryProps {
  versions: AssetVersionSummaryApi[]
  showHashes?: boolean
  className?: string
}

export function AssetVersionHistory({
  versions,
  showHashes = false,
  className,
}: AssetVersionHistoryProps) {
  if (versions.length === 0) {
    return <p className="text-sm text-muted-foreground">No published versions yet.</p>
  }

  const sorted = [...versions].sort((a, b) => b.versionNumber - a.versionNumber)

  return (
    <ol className={className}>
      {sorted.map((version) => (
        <li
          key={version.id}
          className="border-b border-border/50 py-4 first:pt-0 last:border-b-0 last:pb-0"
        >
          <div className="flex flex-wrap items-center gap-2 mb-1.5">
            <span className="text-sm font-semibold text-foreground">v{version.versionNumber}</span>
            {version.isCurrent ? (
              <Badge variant="default" className="text-[10px]">
                Current
              </Badge>
            ) : null}
            <span className="text-[11px] text-muted-foreground">
              {formatShortMonthDate(version.createdAt)}
            </span>
          </div>
          <p className="text-xs text-muted-foreground mb-1">
            {version.fileName} · {formatBytes(version.contentLength)} ·{' '}
            {version.license.displayName}
          </p>
          {version.releaseNotes?.trim() ? (
            <p className="text-sm text-foreground whitespace-pre-wrap break-words [overflow-wrap:anywhere]">
              {version.releaseNotes.trim()}
            </p>
          ) : (
            <p className="text-xs text-muted-foreground italic">No release notes.</p>
          )}
          {showHashes ? <ContentHashDisplay hash={version.contentSha256} className="mt-3" /> : null}
        </li>
      ))}
    </ol>
  )
}
