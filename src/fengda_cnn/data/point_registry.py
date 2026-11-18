"""Point registry and field mapping for the startup boiler dataset."""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class PointSpec:
    point_name: str
    field_name: str
    description: str
    unit: str
    agg: str = "mean"
    is_binary: bool = False
    is_target: bool = False


POINT_SPECS = [
    PointSpec(
        point_name="K0QHL10AA101AI5.UNIT3@NET3",
        field_name="burner_center_gas_valve_pct",
        description="燃烧器中心气调节阀阀位反馈",
        unit="%",
    ),
    PointSpec(
        point_name="K0QHL10AA101AI4.UNIT3@NET3",
        field_name="burner_outer_gas_valve_pct",
        description="燃烧器外环气调节阀阀位反馈",
        unit="%",
    ),
    PointSpec(
        point_name="K0QHL10AA101AI3.UNIT3@NET3",
        field_name="burner_air_damper_pct",
        description="燃烧器风门调节阀阀位反馈",
        unit="%",
    ),
    PointSpec(
        point_name="K0QHL10AN001P.UNIT3@NET3",
        field_name="blower_frequency_hz",
        description="鼓风机变频器频率反馈",
        unit="HZ",
    ),
    PointSpec(
        point_name="K0QHL10AN001I.UNIT3@NET3",
        field_name="blower_current_a",
        description="鼓风机变频器电流",
        unit="A",
    ),
    PointSpec(
        point_name="K0QHL10AA101AI6.UNIT3@NET3",
        field_name="fgr_valve_pct",
        description="FGR调节阀阀位反馈",
        unit="%",
    ),
    PointSpec(
        point_name="K0QHH10AV001P.UNIT3@NET3",
        field_name="fgr_fan_frequency_hz",
        description="FGR风机变频器频率反馈",
        unit="HZ",
    ),
    PointSpec(
        point_name="K0QHL10AA101DO2.UNIT3@NET3",
        field_name="ignition_solenoid_active",
        description="燃烧器点火电磁阀工作",
        unit="",
        agg="max",
        is_binary=True,
    ),
    PointSpec(
        point_name="K0QHB10CP101.UNIT3@NET3",
        field_name="steam_pressure_mpa",
        description="2号启动锅炉过热蒸汽压力",
        unit="MPa",
    ),
    PointSpec(
        point_name="K0QHB10CT302.UNIT3@NET3",
        field_name="steam_temp_c",
        description="2号启动锅炉减温器出口蒸汽温度",
        unit="℃",
    ),
    PointSpec(
        point_name="K0QHB10CF101-COMP.UNIT3@NET3",
        field_name="steam_flow_tph",
        description="2号启动锅炉蒸汽流量补偿值",
        unit="t/h",
    ),
    PointSpec(
        point_name="K0QHA10CT302.UNIT3@NET3",
        field_name="furnace_exit_gas_temp_c",
        description="2号启动锅炉炉膛出口烟气温度",
        unit="℃",
    ),
    PointSpec(
        point_name="K0QHA10CP101.UNIT3@NET3",
        field_name="furnace_exit_gas_pressure_kpa",
        description="2号启动锅炉炉膛出口烟气压力",
        unit="KPa",
    ),
    PointSpec(
        point_name="K0HNE10CQ101.UNIT3@NET3",
        field_name="stack_nox_mg_m3",
        description="2号启动锅炉烟囱NOX排放浓度",
        unit="mg/m3",
        is_target=True,
    ),
    PointSpec(
        point_name="K0HNE10CQ102.UNIT3@NET3",
        field_name="stack_so2_mg_m3",
        description="2号启动锅炉烟囱SO2排放浓度",
        unit="mg/m3",
        is_target=True,
    ),
    PointSpec(
        point_name="K0HNE10CQ103.UNIT3@NET3",
        field_name="stack_o2_pct",
        description="2号启动锅炉烟囱O2排放浓度",
        unit="%",
        is_target=True,
    ),
    PointSpec(
        point_name="K0HNE10CQ104.UNIT3@NET3",
        field_name="stack_dust_mg_m3",
        description="2号启动锅炉烟囱粉尘排放浓度",
        unit="mg/m3",
        is_target=True,
    ),
    PointSpec(
        point_name="K0HNE10CQ105.UNIT3@NET3",
        field_name="stack_no2_mg_m3",
        description="2号启动锅炉烟囱NO2排放浓度",
        unit="mg/m3",
        is_target=True,
    ),
    PointSpec(
        point_name="K0HNE10CT101.UNIT3@NET3",
        field_name="stack_gas_temp_c",
        description="2号启动锅炉烟囱烟气温度",
        unit="℃",
    ),
    PointSpec(
        point_name="K0HNE10CP101.UNIT3@NET3",
        field_name="stack_pressure_pa",
        description="2号启动锅炉烟囱压力",
        unit="Pa",
    ),
    PointSpec(
        point_name="K0HNE10CF101-CAL.UNIT3@NET3",
        field_name="stack_flow_calc_m3_h",
        description="2号启动锅炉烟囱烟气流量计算值",
        unit="m3/h",
    ),
    PointSpec(
        point_name="K0HNE10CM101.UNIT3@NET3",
        field_name="stack_humidity_pct",
        description="2号启动锅炉烟囱湿度",
        unit="%",
    ),
    PointSpec(
        point_name="K0QHH10CF101-1.UNIT3@NET3",
        field_name="natural_gas_inlet_flow_m3_h",
        description="2号启动锅炉天然气入口流量-1",
        unit="m3/h",
    ),
]

POINT_REGISTRY = {spec.point_name: spec for spec in POINT_SPECS}
FIELD_NAMES = [spec.field_name for spec in POINT_SPECS]
FEATURE_COLUMNS = [
    "burner_center_gas_valve_pct",
    "burner_outer_gas_valve_pct",
    "burner_air_damper_pct",
    "blower_frequency_hz",
    "blower_current_a",
    "fgr_valve_pct",
    "fgr_fan_frequency_hz",
    "natural_gas_inlet_flow_m3_h",
]
TARGET_COLUMNS = [
    "stack_nox_mg_m3",
    "stack_o2_pct",
    "stack_no2_mg_m3",
]
