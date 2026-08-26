export type EnvironmentClass = 0 | 1 | 2 | 3

export const environmentName = ['Unknown', 'Dev', 'UAT', 'Prod'] as const

export type AuthMode = 'Windows' | 'SqlLogin'

export interface ConnectionRequest {
  connectionString?: string | null
  server?: string | null
  port?: number | null
  database?: string | null
  authentication?: AuthMode | null
  username?: string | null
  password?: string | null
  trustServerCertificate?: boolean
}

export interface ProbeResponse {
  server: string
  database: string
  productVersion: string
  edition: string
  collation: string
  environment: EnvironmentClass
  readOnly: boolean
  tableCount: number
  viewCount: number
  routineCount: number
  warnings: string[]
}

export interface ObjectSummary {
  id: string
  type: string
  schema: string
  name: string
  qualifiedName: string
  kind: 'Same' | 'SourceOnly' | 'TargetOnly' | 'Different'
  summary: string
  changedChildren: number
}

export interface TypeCount {
  type: string
  changed: number
  total: number
}

export interface CompareResponse {
  id: string
  sourceDatabase: string
  targetDatabase: string
  sourceEnvironment: EnvironmentClass
  targetEnvironment: EnvironmentClass
  targetReadOnly: boolean
  durationMs: number
  targetIsEmpty: boolean
  warnings: string[]
  counts: TypeCount[]
  objects: ObjectSummary[]
}

export interface PropertyDto {
  property: string
  source: string | null
  target: string | null
}

export interface ObjectDetail {
  summary: ObjectSummary
  properties: PropertyDto[]
  children: ObjectSummary[]
  childProperties: PropertyDto[]
  sourceDefinition: string | null
  targetDefinition: string | null
  plannedStatements: string[]
}

export interface StepDto {
  phase: string
  description: string
  sql: string
}

export interface ScriptResponse {
  sql: string
  stepCount: number
  byteSize: number
  exceedsReviewableSize: boolean
  steps: StepDto[]
  deleteWarnings: string[]
}

async function post<T>(url: string, body: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    throw new Error(await readError(response))
  }

  return (await response.json()) as T
}

async function readError(response: Response): Promise<string> {
  try {
    const problem = await response.json()
    return problem.detail ?? problem.title ?? `Request failed (${response.status})`
  } catch {
    return `Request failed (${response.status})`
  }
}

export const api = {
  probe: (connection: ConnectionRequest) => post<ProbeResponse>('/api/probe', connection),
  compare: (source: ConnectionRequest, target: ConnectionRequest) =>
    post<CompareResponse>('/api/compare', { source, target }),
  detail: async (compareId: string, objectId: string) => {
    const response = await fetch(`/api/compare/${compareId}/objects/${objectId}`)
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as ObjectDetail
  },
  script: (compareId: string) => post<ScriptResponse>(`/api/compare/${compareId}/script`, {}),
}

export interface ApplyResponse {
  outcome: 'Committed' | 'RolledBack' | 'Blocked' | 'Drifted'
  message: string
  stepCount: number
  durationMs: number
  serverMessage: string | null
  errorNumber: number | null
  blockers: string[]
  destructiveSteps: string[]
}

export interface RunLogEntryDto {
  id: string
  at: string
  action: string
  route: string
  outcome: string
  stepCount: number
  durationMs: number
  serverMessage: string | null
}

export const applyApi = {
  apply: (compareId: string, confirmation: string, allowDestructive: boolean) =>
    postJson<ApplyResponse>(`/api/compare/${compareId}/apply`, { confirmation, allowDestructive }),
  runs: async () => {
    const response = await fetch('/api/runs')
    if (!response.ok) throw new Error('Could not read the run log')
    return (await response.json()) as RunLogEntryDto[]
  },
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw new Error(await readError(response))
  return (await response.json()) as T
}

export interface TableRow {
  qualifiedName: string
  sourceRows: number
  sourceBytes: number
  targetRows: number
  targetBytes: number
  keyColumns: string[]
  hasKey: boolean
  onBothSides: boolean
}

export interface VolumeSummary {
  sourceDataBytes: number
  sourceLogBytes: number
  sourceRows: number
  targetDataBytes: number
  targetLogBytes: number
  targetRows: number
  tables: TableRow[]
}

export interface CellDiff {
  column: string
  source: string | null
  target: string | null
}

export interface RowDiffDto {
  key: string
  display: string
  classification: 'Insert' | 'Update' | 'Delete'
  changes: CellDiff[]
  unchangedColumns: number
}

export type TableDataMode = 'SchemaOnly' | 'AllRows' | 'TopN' | 'Filter'

export interface DataCompareResponse {
  table: string
  mode: TableDataMode
  deletesSuppressed: boolean
  keyColumns: string[]
  comparedColumns: string[]
  excludedColumns: CellDiff[]
  insertCount: number
  updateCount: number
  deleteCount: number
  sameCount: number
  transferBytesEstimate: number
  footprintBytes: number
  rows: RowDiffDto[]
  warning: string | null
}

export const dataApi = {
  volume: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/volume`)
    if (!response.ok) throw new Error('Could not read database sizes')
    return (await response.json()) as VolumeSummary
  },
  compare: (compareId: string, table: string, mode: TableDataMode, topCount: number, filter: string | null) =>
    postJson<DataCompareResponse>(`/api/compare/${compareId}/data`, { table, mode, topCount, filter }),
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} kB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}

export interface FkNode {
  qualifiedName: string
  band: number
  state: string
  note: string
  rows: number
  bytes: number
}

export interface FkEdge {
  from: string
  to: string
  name: string
  columns: string
  nullable: boolean
  partOfCycle: boolean
}

export interface FkMapResponse {
  focus: string
  depth: number
  nodes: FkNode[]
  edges: FkEdge[]
  cycles: string[]
  notes: string[]
}

export const fkApi = {
  map: async (compareId: string, table: string, depth: number) => {
    const response = await fetch(
      `/api/compare/${compareId}/fk?table=${encodeURIComponent(table)}&depth=${depth}`,
    )
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as FkMapResponse
  },
}

export interface SelectedTable {
  table: string
  mode: string
  topCount: number
}

export const planApi = {
  select: (compareId: string, table: string, selected: boolean, mode: TableDataMode, topCount: number) =>
    postJson<{ selected: SelectedTable[] }>(`/api/compare/${compareId}/data/select`, {
      table,
      selected,
      mode,
      topCount,
    }),
  selection: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/data/select`)
    if (!response.ok) throw new Error('Could not read the data selection')
    return (await response.json()) as { selected: SelectedTable[] }
  },
}

export interface TableScanRow {
  table: string
  comparable: boolean
  reason: string | null
  differs: boolean
  sourceRows: number
  targetRows: number
  rowDelta: number
}

export interface TableScanResponse {
  durationMs: number
  scanned: number
  differing: number
  notComparable: number
  skipped: number
  maxTableBytes: number
  tables: TableScanRow[]
}

export const scanApi = {
  run: (compareId: string, maxTableBytes: number) =>
    postJson<TableScanResponse>(
      `/api/compare/${compareId}/data/scan?maxTableBytes=${maxTableBytes}`,
      {},
    ),
  cached: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/data/scan`)
    if (response.status === 204) return null
    if (!response.ok) throw new Error('Could not read the scan')
    return (await response.json()) as TableScanResponse
  },
}

export interface SchemaSelectionResponse {
  selected: string[]
  differing: number
  dataTables: number
}

export const schemaApi = {
  select: (compareId: string, objectId: string, selected: boolean) =>
    postJson<SchemaSelectionResponse>(`/api/compare/${compareId}/schema/select`, { objectId, selected }),
  selectAll: (compareId: string, selected: boolean) =>
    postJson<SchemaSelectionResponse>(
      `/api/compare/${compareId}/schema/select-all?selected=${selected}`,
      {},
    ),
  selection: async (compareId: string) => {
    const response = await fetch(`/api/compare/${compareId}/schema/select`)
    if (!response.ok) throw new Error('Could not read the schema selection')
    return (await response.json()) as SchemaSelectionResponse
  },
}

export interface KeyCandidate {
  column: string
  dataType: string
  nullable: boolean
}

export interface KeyChoiceResponse {
  table: string
  chosen: string[]
  fromPrimaryKey: boolean
  candidates: KeyCandidate[]
  problem: string | null
}

export const keyApi = {
  options: async (compareId: string, table: string) => {
    const response = await fetch(
      `/api/compare/${compareId}/data/key?table=${encodeURIComponent(table)}`,
    )
    if (!response.ok) throw new Error(await readError(response))
    return (await response.json()) as KeyChoiceResponse
  },
  choose: (compareId: string, table: string, columns: string[]) =>
    postJson<KeyChoiceResponse>(`/api/compare/${compareId}/data/key`, { table, columns }),
}
