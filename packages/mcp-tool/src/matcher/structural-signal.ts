// T2.4: layer-name-to-asset-name similarity, component-instance-to-prefab
// match, description similarity, combined into a single structural score -
// the counterpart to visual-signal.ts's pixel comparison (R5's two-factor
// gate needs both to disagree/agree independently, not one signal that's
// secretly derived from the other).
//
// Weights are a rough first pass, checked against golden-elements.json /
// golden-matches.json (not formally tuned - that's T2.7, once gate.ts can
// score end-to-end). WEIGHT_INSTANCE starts low (not 1/3) because it turned
// out to be actively counterproductive at a naive equal weighting: scoring
// button_continue's candidates with instance/prefab weighted at 0.15 ranked
// the generic untinted UIElements__ButtonFrame prefab above the actually-
// correct UIElements__button_green sprite, because button_continue is a
// Figma instance (true) matching a plain sprite (the common case per the
// comment below) - the prefab bonus outweighed color-word evidence in
// nameScore/descScore. Dropping it to 0.05 fixed that and brought 4 of 5
// real golden-matches.json "matched" cases to rank #1 among their own
// candidates.ts top-10 (up from 2 of 5) - see the module comment on
// nameSimilarity for the one remaining known miss.

import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";
import { jaccardSimilarity, tokenize } from "./tokenize.js";

const WEIGHT_NAME = 0.4;
const WEIGHT_INSTANCE = 0.05;
const WEIGHT_DESCRIPTION = 0.55;

function elementNameTokens(element: ElementTree["elements"][number]): Set<string> {
  return tokenize([element.id, element.type].join(" "));
}

function candidateIdentityTokens(entry: CatalogEntry[number]): Set<string> {
  const basename = entry.path.split("/").pop() ?? entry.path;
  return tokenize([entry.id, basename, entry.role].join(" "));
}

// Plain (unweighted) token overlap, unlike candidates.ts's IDF-weighted
// version - this scores ONE candidate the retrieval pre-filter already
// selected, not ranking across the whole catalog, so there's no catalog-
// wide document-frequency context available to weight against. Known
// consequence, worth flagging for T2.7: a generic shared word (e.g. the
// "icon_" prefix on ~20 catalog entries) can still out-rank a rarer correct
// token within this signal alone - candidates.ts fixes this for retrieval
// specifically because it sees the whole catalog; this per-pair signal
// doesn't, and relies on visual-signal.ts + the margin/agreement gate
// (T2.5) to catch what it misses (confirmed against golden-elements.json:
// "icon_glow" scores its correct match, UIElements__ui_img_glow, lowest of
// its own top-10 candidates here).
function nameSimilarity(element: ElementTree["elements"][number], candidate: CatalogEntry[number]): number {
  return jaccardSimilarity(elementNameTokens(element), candidateIdentityTokens(candidate));
}

// Figma's is_component_instance is a template hint (R9), not a hard rule:
// real golden-matches.json cases show is_component_instance: true elements
// matching plain SPRITE catalog entries just as often as prefabs
// (icon_heart, icon_glow, button_x are all instances matched to sprites -
// only button_upgrade matches a prefab). So this rewards the prefab+
// instance combination without punishing the equally-valid instance+sprite
// one; a non-instance element matching a prefab is the more surprising
// combination and gets a mild penalty instead.
function instanceScore(element: ElementTree["elements"][number], candidate: CatalogEntry[number]): number {
  if (candidate.type !== "prefab") return 0.5;
  return element.is_component_instance ? 1 : 0.3;
}

// Element's own visual_description (+ any text_content) against the
// candidate's FULL textual identity - its id/path/role AND its description,
// not description-only. That's deliberate, not a shortcut: catalog
// descriptions are currently empty (T1.7 skipped, see HANDOFF.md), and a
// element's description is often the only place a distinguishing word like
// a color actually shows up (e.g. button_continue's description says
// "green rounded button" - "green" isn't in element.id, but it IS in
// candidate "UIElements__button_green"'s own id). Comparing description
// against id/path/role too catches that without waiting on T1.7, and still
// picks up real description-to-description matches once T1.7 runs (that
// text just becomes additional candidate-side tokens).
function descriptionSimilarity(element: ElementTree["elements"][number], candidate: CatalogEntry[number]): number {
  const elementText = element.text_content
    ? `${element.visual_description} ${element.text_content}`
    : element.visual_description;
  const candidateText = [candidate.id, candidate.path, candidate.role, candidate.visual_description].join(" ");
  return jaccardSimilarity(tokenize(elementText), tokenize(candidateText));
}

export function structuralSignal(
  element: ElementTree["elements"][number],
  candidate: CatalogEntry[number],
): number {
  const nameScore = nameSimilarity(element, candidate);
  const instScore = instanceScore(element, candidate);
  const descScore = descriptionSimilarity(element, candidate);

  return nameScore * WEIGHT_NAME + instScore * WEIGHT_INSTANCE + descScore * WEIGHT_DESCRIPTION;
}
