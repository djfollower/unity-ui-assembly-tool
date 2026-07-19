// Strips optional markdown code fences from an agent's text response before
// JSON.parse - shared by reduce.ts and visual-signal.ts, both of which ask
// for JSON-only output but can't fully rely on that being literal (agents
// wrap JSON in ```json fences by habit often enough to be worth handling).

export function parseJsonResponse<T>(text: string): T {
  const fenced = text.match(/```(?:json)?\s*([\s\S]*?)```/);
  const jsonText = (fenced ? fenced[1] : text).trim();

  try {
    return JSON.parse(jsonText) as T;
  } catch (err) {
    throw new Error(`parseJsonResponse: failed to parse agent output as JSON: ${(err as Error).message}\n---\n${text}`);
  }
}
