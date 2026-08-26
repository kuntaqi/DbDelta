export type EnvironmentClass = 0 | 1 | 2 | 3

export const environmentName = ['Unknown', 'Dev', 'UAT', 'Prod'] as const

export interface ConnectionRequest {
  server: string
  database: string
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
  collationWarning: string | null
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
  script: (compareId: string, include: string[]) =>
    post<ScriptResponse>(`/api/compare/${compareId}/script`, { include }),
}
