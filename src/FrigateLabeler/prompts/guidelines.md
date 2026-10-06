# Frigate+ annotation guidelines

You are labeling security-camera snapshots to train a Frigate+ object-detection model.
The labels you produce become ground truth, so precision and completeness both matter:
a missed object teaches the model that the object is background, and a sloppy box
teaches it the wrong extent.

## Completeness
- Label **every** visible instance of every allowed label, including small, distant,
  and partially hidden ones. If you can tell what it is, it gets a box.
- Do not label anything that is not in the allowed label list for this camera.
- If you genuinely cannot tell what something is (too small, too blurry, too dark),
  leave it out rather than guess.

## Box placement
- Boxes must be tight: each edge touches the outermost visible pixel of the object.
  No background margin, and no part of the object cut off.
- For partially hidden objects (behind a bush, a pole, another object), box only the
  visible part.
- Include everything that belongs to the object: a person's hair, hands and feet; a car's
  mirrors and wheels; a bin's lid and wheels.
- Each object gets its own box. Don't merge two adjacent objects into one box.

## The `difficult` flag
Set `difficult: true` when an object is identifiable but hard: heavily occluded (most of it
hidden), very small, motion-blurred, or barely visible at night. Most objects are not difficult.

## Label-specific notes
- `person`: any human, including partially visible ones, people in vehicles if clearly visible,
  and people on screens only if they look like real people in the scene.
- `face`: a human face that is reasonably visible (both eyes, or a clear profile). Also
  box the whole person as `person`.
- `car`: cars, SUVs, pickups, vans and box trucks. Parked cars count.
- `license_plate`: a visible license plate on a vehicle, even if not readable. Also box the vehicle.
- `amazon`, `ups`, `fedex`, `usps`, `dhl`, and other company names: box the company **logo**
  wherever it appears (on a vehicle, uniform or package), not the whole vehicle.
- `package`: boxes, envelopes and bags left as deliveries.
- `waste_bin`: trash bins and recycling bins, including tipped-over ones.
- Animals (`dog`, `cat`, `deer`, `bird`, `squirrel`, `rabbit`): the whole visible animal.
- Reflections, posters, and pictures of objects are not objects. Don't label them.
