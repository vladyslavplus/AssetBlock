'use client'

import { Download, Loader2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { downloadAnalyticsSalesExport } from '@/lib/analytics/analytics-export'
import type { AnalyticsProductTypeFilter, AnalyticsUtcRange } from '@/lib/analytics/analytics-types'
import { ApiRequestError } from '@/lib/http/api-client'

interface AnalyticsExportButtonProps {
  range: AnalyticsUtcRange
  productType: AnalyticsProductTypeFilter
  disabled?: boolean
}

export function AnalyticsExportButton({
  range,
  productType,
  disabled = false,
}: AnalyticsExportButtonProps) {
  const [exporting, setExporting] = useState(false)
  const abortRef = useRef<AbortController | null>(null)

  useEffect(() => {
    return () => {
      abortRef.current?.abort()
    }
  }, [])

  async function handleExport() {
    if (exporting) return

    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller
    setExporting(true)

    try {
      await downloadAnalyticsSalesExport(range, productType, controller.signal)
      toast.success('Sales export downloaded.')
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        return
      }
      if (error instanceof Error && error.name === 'AbortError') {
        return
      }
      const message =
        error instanceof ApiRequestError
          ? error.message
          : 'Could not export sales. Check your connection and try again.'
      toast.error(message)
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null
      }
      setExporting(false)
    }
  }

  return (
    <Button
      type="button"
      variant="outline"
      size="sm"
      disabled={disabled || exporting}
      onClick={() => void handleExport()}
      aria-busy={exporting}
    >
      {exporting ? (
        <Loader2 className="mr-2 size-4 animate-spin motion-reduce:animate-none" aria-hidden />
      ) : (
        <Download className="mr-2 size-4" aria-hidden />
      )}
      {exporting ? 'Exporting…' : 'Export sales CSV'}
    </Button>
  )
}
