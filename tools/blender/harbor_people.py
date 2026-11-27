"""Downtown Calloway residents, protagonist, and factions built on MakeHuman + Harbor's 51-bone rig."""

import makehuman_person


def freight_clerk():
    """Tomas Reyes — Protagonist (Freight Clerk) with 9 embedded Mixamo mocap clips."""
    return makehuman_person.build_makehuman_clerk("chr_harbor_clerk", height=1.76)


def officer():
    """Mara Vale — Port Officer in midnight-navy patrol blazer and coastal-blue uniform shirt."""
    return makehuman_person.build_mara_vale("chr_harbor_officer", height=1.79)


def rival_crew():
    """Niko Voss — Saltline rival crew heavy in oxblood shell jacket and washed dark denim."""
    return makehuman_person.build_niko_voss("chr_harbor_crew", height=1.91)


def rook():
    """Rook — Waterfront courier in sea-green waxed jacket and dark slate field trousers."""
    return makehuman_person.build_rook_courier("chr_harbor_rook", height=1.82)


def dockhand():
    """Amara Cole — Dock electrician (Pedestrian Slot 0) in signal-orange work knit and olive trousers."""
    return makehuman_person.build_amara_cole("chr_harbor_dockhand", height=1.69)


def stevedore():
    """Mateo Silva — Quay stevedore (Pedestrian Slot 1) in marine-slate bib overalls and mustard shirt."""
    return makehuman_person.build_mateo_silva("chr_harbor_stevedore", height=1.84)


def customs_broker():
    """Arthur Pendelton — Customs broker (Pedestrian Slot 3) in burgundy-tweed suit and reading glasses."""
    return makehuman_person.build_arthur_pendelton("chr_harbor_broker", height=1.75)


ROSTER = [
    ("characters/chr_harbor_clerk.glb", freight_clerk),
    ("characters/chr_harbor_officer.glb", officer),
    ("characters/chr_harbor_crew.glb", rival_crew),
    ("characters/chr_harbor_rook.glb", rook),
    ("characters/chr_harbor_dockhand.glb", dockhand),
    ("characters/chr_harbor_stevedore.glb", stevedore),
    ("characters/chr_harbor_broker.glb", customs_broker),
]
