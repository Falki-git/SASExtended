namespace SASExtended.Models;

public enum AttitudeMode
{
    None,
    KillRot,
    Maneuver,

    OrbitPrograde,
    OrbitNormal,
    OrbitRadialIn,
    OrbitRetrograde,
    OrbitAntiNormal,
    OrbitRadialOut,

    SurfaceSvelPlus,
    SurfaceHvelPlus,
    SurfaceSurf,
    SurfaceSvelMinus,
    SurfaceHvelMinus,
    SurfaceUp,

    TargetPlus,
    TargetRvelPlus,
    TargetParPlus,
    TargetMinus,
    TargetRvelMinus,
    TargetParMinus,

    SpecialStarPlus,
    SpecialStarMinus
}