// T2.3: pure-JS re-render of a catalog candidate's thumbnail at an
// arbitrary target size (the element's rect), respecting its 9-slice border
// instead of naively stretching - a naive stretch is exactly what would
// distort a 9-sliced asset's corners and defeat the point of this signal
// (deliberately avoids a Unity round-trip - the thumbnail + render metadata
// already in catalog.json is everything this needs).
//
// Tint is NOT reapplied here: catalog-entry.schema.json's thumbnail is
// already the RENDERED asset "at canonical size, post 9-slice/tint" (R14) -
// RenderedThumbnail.cs (T1.5) baked tint in when it produced that PNG.
//
// Unit reconciliation (the part worth documenting, since it isn't obvious
// from the schema alone): `render.border` is in the sprite's native texture
// pixels; `render.native_size` is NOT reliably the same thing - for prefab
// catalog entries, RenderMetadataProbe.cs reads native_size off the
// authored RectTransform, which need not match the underlying sprite's
// pixel size 1:1. The one quantity that IS scale-invariant and asset-
// intrinsic is border / (ppu * ppu_multiplier): Unity keeps a Sliced
// Image's border a CONSTANT size in UI units regardless of the element's
// box size (that's the entire point of 9-slicing) - so that division gives
// the border thickness directly in canvas_reference units, usable as pixel
// border width on the output canvas with no further scaling. Getting the
// border's position WITHIN the thumbnail image (a different pixel space -
// the thumbnail was fit-to-square at some canonicalSize, not rendered at
// native_size 1:1) is solved by measuring the thumbnail's actual non-
// transparent content bounding box directly from its pixels rather than
// trusting a recomputed canonicalSize/native_size ratio - robust to
// whatever canonical size T1.5 used, and self-consistent because that same
// bounding box is what T1.5's own fit-to-square scale produced.

import { readFile } from "node:fs/promises";
import sharp from "sharp";
import type { CatalogEntry } from "@ui-assembler-slice/contracts";

export interface TargetSize {
  w: number;
  h: number;
}

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface Border {
  left: number;
  bottom: number;
  right: number;
  top: number;
}


// Bounding box of non-transparent pixels - see the module comment on why
// this is measured rather than recomputed from canonicalSize/native_size.
// Also returns the full image dimensions, for extractRegion's defensive
// clamp (see its comment).
async function detectContentBBox(png: Buffer): Promise<{ bbox: Rect; imageWidth: number; imageHeight: number }> {
  const { data, info } = await sharp(png).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const { width, height, channels } = info;

  let minX = width;
  let minY = height;
  let maxX = -1;
  let maxY = -1;

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const alpha = data[(y * width + x) * channels + 3];
      if (alpha > 0) {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
  }

  if (maxX < minX || maxY < minY) {
    throw new Error("renderCandidateAtSize: thumbnail is fully transparent - cannot locate content bounds");
  }

  return { bbox: { x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1 }, imageWidth: width, imageHeight: height };
}

// Shrinks border thicknesses proportionally so left+right <= boundW and
// top+bottom <= boundH, mirroring how Unity itself degrades a Sliced image
// asked to render smaller than its own border (corners overlap/compress
// rather than the middle going negative).
function clampBorder(border: Border, boundW: number, boundH: number): Border {
  const horizontalScale = border.left + border.right > boundW && border.left + border.right > 0
    ? boundW / (border.left + border.right)
    : 1;
  const verticalScale = border.top + border.bottom > boundH && border.top + border.bottom > 0
    ? boundH / (border.top + border.bottom)
    : 1;
  return {
    left: border.left * horizontalScale,
    right: border.right * horizontalScale,
    top: border.top * verticalScale,
    bottom: border.bottom * verticalScale,
  };
}

// region is expected to already be integer-valued and in-bounds (see
// sliceRects's edge-based rounding) - the clamp here is a defensive second
// layer, not the primary fix: sharp's extract() throws rather than clamps
// on an out-of-bounds region, and independently-rounded x/width pairs can
// overflow a source image's actual edge by a pixel when content fills the
// image edge-to-edge with no transparent margin (found by fuzzing every
// real catalog entry against a range of target sizes - UIElements__ui_
// popup_frame's content fills its whole 256x256 thumbnail with zero
// padding, so contentBBox.x + contentBBox.w lands exactly on the image
// boundary, and rounding its right-column x and width separately pushed
// their sum one pixel past it).
async function extractRegion(png: Buffer, region: Rect, imageWidth: number, imageHeight: number): Promise<Buffer | null> {
  const left = Math.max(0, Math.min(Math.round(region.x), imageWidth));
  const top = Math.max(0, Math.min(Math.round(region.y), imageHeight));
  const width = Math.max(0, Math.min(Math.round(region.w), imageWidth - left));
  const height = Math.max(0, Math.min(Math.round(region.h), imageHeight - top));
  if (width <= 0 || height <= 0) return null;
  return sharp(png).extract({ left, top, width, height }).toBuffer();
}

async function resizeTo(png: Buffer, w: number, h: number): Promise<Buffer> {
  const width = Math.max(1, Math.round(w));
  const height = Math.max(1, Math.round(h));
  return sharp(png).resize(width, height, { fit: "fill" }).toBuffer();
}

// Rounds each of the 4 boundary coordinates ONCE (not each rect's x/width
// independently) and derives widths from consecutive rounded edges - this
// is what guarantees adjacent slices tile exactly with no gap/overlap and,
// critically, that the outer edge never rounds past the source image's
// actual boundary (see extractRegion's comment for the bug this fixes:
// rounding x and width separately can each round "outward" and overshoot).
function roundedEdges(start: number, size: number, innerA: number, innerB: number): number[] {
  return [start, start + innerA, start + size - innerB, start + size].map((edge) => Math.round(edge));
}

// Nine source rects (within the thumbnail's content bbox) paired with their
// destination rects (within the output canvas) - corners are 1:1 conceptual
// copies (resized only incidentally, see the unit-reconciliation note
// above), edges stretch along one axis, the middle stretches both.
function sliceRects(contentBBox: Rect, srcBorder: Border, target: TargetSize, destBorder: Border) {
  const srcColEdges = roundedEdges(contentBBox.x, contentBBox.w, srcBorder.left, srcBorder.right);
  const srcRowEdges = roundedEdges(contentBBox.y, contentBBox.h, srcBorder.top, srcBorder.bottom);
  const destColEdges = roundedEdges(0, target.w, destBorder.left, destBorder.right);
  const destRowEdges = roundedEdges(0, target.h, destBorder.top, destBorder.bottom);

  const slices: Array<{ src: Rect; dest: Rect }> = [];
  for (let row = 0; row < 3; row++) {
    for (let col = 0; col < 3; col++) {
      slices.push({
        src: {
          x: srcColEdges[col], y: srcRowEdges[row],
          w: srcColEdges[col + 1] - srcColEdges[col], h: srcRowEdges[row + 1] - srcRowEdges[row],
        },
        dest: {
          x: destColEdges[col], y: destRowEdges[row],
          w: destColEdges[col + 1] - destColEdges[col], h: destRowEdges[row + 1] - destRowEdges[row],
        },
      });
    }
  }
  return slices;
}

// Renders a catalog candidate's already-baked thumbnail at an arbitrary
// target size, respecting its 9-slice border for image_type "Sliced" (and
// stretching fully for "Simple"). "Tiled"/"Filled" don't occur in this
// catalog (verified against .cache/catalog.json - Simple/Sliced only) and
// fall back to the Simple behavior rather than being silently wrong in a
// more exotic way; revisit if a Tiled/Filled asset shows up.
export async function renderCandidateAtSize(
  candidate: CatalogEntry[number],
  target: TargetSize,
): Promise<Buffer> {
  const targetW = Math.max(1, Math.round(target.w));
  const targetH = Math.max(1, Math.round(target.h));
  // Already resolved to an absolute path by loadCatalog() - see its comment.
  const thumbnail = await readFile(candidate.thumbnail_path);
  const { bbox: contentBBox, imageWidth, imageHeight } = await detectContentBBox(thumbnail);

  if (candidate.render.image_type !== "Sliced") {
    const content = await extractRegion(thumbnail, contentBBox, imageWidth, imageHeight);
    if (!content) {
      throw new Error(`renderCandidateAtSize: ${candidate.id} has a degenerate content region`);
    }
    return resizeTo(content, targetW, targetH);
  }

  const { native_size, border, ppu, ppu_multiplier } = candidate.render;
  // canvas_reference units (R3/normalize.ts) are Unity UI canvas units, not
  // world units - the conversion from sprite texture pixels needs the
  // Canvas Scaler's Reference Pixels Per Unit (RPPU), not just the sprite's
  // own ppu: borderCanvasUnits = borderTexturePx * (RPPU / spritePpu) *
  // ppuMultiplier. RPPU isn't in the schema (R3's CanvasScalerConfig is
  // hardcoded per-fixture, same as normalize.ts) - Unity's default is 100,
  // which also matches every ppu value actually observed in this catalog
  // (see .cache/catalog.json), so RPPU/ppu cancels to 1 for this fixture.
  // Empirically verified: dividing by ppu directly (treating it as already
  // in canvas units) instead collapsed borders to sub-pixel widths and
  // erased the button's rounded frame entirely - caught by rendering
  // UIElements__button_frame_blue and looking at the output.
  const REFERENCE_PIXELS_PER_UNIT = 100;
  const borderUiUnits: Border = {
    left: (border[0] * REFERENCE_PIXELS_PER_UNIT / ppu) * ppu_multiplier,
    bottom: (border[1] * REFERENCE_PIXELS_PER_UNIT / ppu) * ppu_multiplier,
    right: (border[2] * REFERENCE_PIXELS_PER_UNIT / ppu) * ppu_multiplier,
    top: (border[3] * REFERENCE_PIXELS_PER_UNIT / ppu) * ppu_multiplier,
  };
  const thumbnailScale = (contentBBox.w / native_size.w + contentBBox.h / native_size.h) / 2;
  const srcBorder: Border = {
    left: border[0] * thumbnailScale,
    bottom: border[1] * thumbnailScale,
    right: border[2] * thumbnailScale,
    top: border[3] * thumbnailScale,
  };

  const destBorder = clampBorder(borderUiUnits, targetW, targetH);
  const clampedSrcBorder = clampBorder(srcBorder, contentBBox.w, contentBBox.h);

  const slices = sliceRects(contentBBox, clampedSrcBorder, { w: targetW, h: targetH }, destBorder);

  const composites: Array<{ input: Buffer; left: number; top: number }> = [];
  for (const slice of slices) {
    const source = await extractRegion(thumbnail, slice.src, imageWidth, imageHeight);
    if (!source) continue;
    const resized = await resizeTo(source, slice.dest.w, slice.dest.h);
    composites.push({ input: resized, left: Math.round(slice.dest.x), top: Math.round(slice.dest.y) });
  }

  return sharp({ create: { width: targetW, height: targetH, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
    .composite(composites)
    .png()
    .toBuffer();
}
