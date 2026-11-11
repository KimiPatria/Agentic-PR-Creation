import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import type { Option } from '../api'
import { api } from '../api'
import { inputClass } from '../ui'

/** All entries of a small SAP list (plants, units, material groups), cached for the session. */
export function useMasterList(kind: string) {
  return useQuery({ queryKey: ['master-data', kind, 'all'], queryFn: () => api.masterData(kind, '', 200), staleTime: 10 * 60_000 })
}

/** A dropdown over a small SAP list. */
export function MasterSelect({
  kind,
  value,
  onChange,
  disabled,
  filter,
  placeholder = 'Choose',
}: {
  kind: string
  value: string | null | undefined
  onChange: (code: string | null) => void
  disabled?: boolean
  filter?: (option: Option) => boolean
  placeholder?: string
}) {
  const list = useMasterList(kind)
  const options = (list.data ?? []).filter((option) => !filter || filter(option) || option.code === value)

  return (
    <select className={inputClass} value={value ?? ''} disabled={disabled} onChange={(event) => onChange(event.target.value || null)}>
      <option value="">{list.isPending ? 'Loading…' : placeholder}</option>
      {value && !options.some((o) => o.code === value) && <option value={value}>{value}</option>}
      {options.map((option) => (
        <option key={option.code} value={option.code}>
          {option.code} · {option.name}
        </option>
      ))}
    </select>
  )
}

/**
 * A search box over a large SAP set (materials, cost centers, WBS elements, assets). The requestor
 * types part of a code or name and picks a result; free text that matches nothing is not accepted.
 */
export function MasterSearch({
  kind,
  value,
  onChange,
  disabled,
  placeholder,
}: {
  kind: string
  value: string | null | undefined
  onChange: (code: string | null, option?: Option) => void
  disabled?: boolean
  placeholder?: string
}) {
  const [text, setText] = useState(value ?? '')
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const box = useRef<HTMLDivElement>(null)

  // Follow the value when it is changed from outside (a suggestion applied, a line reset).
  useEffect(() => {
    if (!open) setText(value ?? '')
  }, [value, open])

  // Search a moment after the requestor stops typing.
  useEffect(() => {
    const timer = setTimeout(() => setQuery(text.trim()), 250)
    return () => clearTimeout(timer)
  }, [text])

  const results = useQuery({
    queryKey: ['master-data', kind, 'search', query],
    queryFn: () => api.masterData(kind, query === value ? '' : query, 12),
    enabled: open,
    staleTime: 60_000,
  })

  // The name of the selected code, shown under the box.
  const selected = useQuery({
    queryKey: ['master-data', kind, 'search', value ?? ''],
    queryFn: () => api.masterData(kind, value ?? '', 12),
    enabled: !!value,
    staleTime: 10 * 60_000,
  })
  const selectedName = selected.data?.find((o) => o.code.toLowerCase() === value?.toLowerCase())?.name

  const close = () => {
    setOpen(false)
    setText(value ?? '')
  }

  return (
    <div
      ref={box}
      className="relative"
      onBlur={(event) => {
        if (!box.current?.contains(event.relatedTarget as Node | null)) close()
      }}
    >
      <input
        className={inputClass}
        value={text}
        disabled={disabled}
        placeholder={placeholder ?? 'Type to search'}
        onFocus={() => setOpen(true)}
        onChange={(event) => {
          setText(event.target.value)
          setOpen(true)
          if (event.target.value === '') onChange(null)
        }}
        onKeyDown={(event) => event.key === 'Escape' && close()}
        role="combobox"
        aria-expanded={open}
        aria-autocomplete="list"
      />
      {value && selectedName && !open && <span className="mt-1 block truncate text-xs text-slate-500">{selectedName}</span>}

      {open && !disabled && (
        <ul className="absolute z-20 mt-1 max-h-64 w-full min-w-64 overflow-y-auto rounded-md border border-slate-200 bg-white py-1 shadow-lg" role="listbox">
          {results.isPending && <li className="px-3 py-2 text-xs text-slate-500">Searching…</li>}
          {results.isError && <li className="px-3 py-2 text-xs text-rose-700">{results.error.message}</li>}
          {results.data?.length === 0 && <li className="px-3 py-2 text-xs text-slate-500">Nothing in SAP matches “{query}”.</li>}
          {results.data?.map((option) => (
            <li key={option.code} role="option" aria-selected={option.code === value}>
              <button
                type="button"
                className="block w-full px-3 py-1.5 text-left text-sm hover:bg-teal-50"
                onClick={() => {
                  onChange(option.code, option)
                  setText(option.code)
                  setOpen(false)
                }}
              >
                <span className="font-medium">{option.code}</span>
                <span className="ml-2 text-slate-600">{option.name}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
