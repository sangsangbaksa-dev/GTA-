export const assetUrl = path => new URL(path, document.baseURI).href;

export const DISTRICT_COLORS = {
  harbor: '#789b9e',
  city: '#9cbabe',
  low_income: '#b9a183',
  desert: '#c38e5d',
  military: '#a5a587',
  forest: '#618f73',
  wealthy: '#87a783',
};

export function districtAt(layout, position) {
  const districts = layout.districts ?? [];
  const contains = ({ center, size }) =>
    Math.abs(position.x - center[0]) <= size[0] / 2 && Math.abs(position.z - center[1]) <= size[1] / 2;
  return districts.find(contains)?.name ?? 'Port Calloway';
}
