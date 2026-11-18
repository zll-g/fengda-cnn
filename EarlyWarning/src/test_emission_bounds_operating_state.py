import unittest

import numpy as np

from train_emission_bounds import EffectiveBurningConfig, INPUT_FIELDS, effective_burning_mask


class EffectiveBurningMaskTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = EffectiveBurningConfig()

    def rows(self, count: int) -> np.ndarray:
        values = np.zeros((count, len(INPUT_FIELDS)), dtype=np.float32)
        values[:, INPUT_FIELDS.index("natural_gas_inlet_flow_m3_h")] = 1.28
        values[:, INPUT_FIELDS.index("blower_frequency_hz")] = 1.32
        values[:, INPUT_FIELDS.index("blower_current_a")] = 2.28
        return values

    def set_firing(self, values: np.ndarray, begin: int, end: int) -> None:
        values[begin:end, INPUT_FIELDS.index("natural_gas_inlet_flow_m3_h")] = 100.0
        values[begin:end, INPUT_FIELDS.index("blower_frequency_hz")] = 10.0
        values[begin:end, INPUT_FIELDS.index("blower_current_a")] = 10.0

    def test_enters_only_after_full_dwell_and_uses_confirmation_history(self) -> None:
        values = self.rows(80)
        self.set_firing(values, 0, 59)
        valve_running = np.ones(len(values), dtype=bool)
        mask = effective_burning_mask(values, valve_running, self.config)
        self.assertFalse(mask.any())

        self.set_firing(values, 0, 60)
        mask = effective_burning_mask(values, valve_running, self.config)
        self.assertTrue(mask[:60].all())
        self.assertFalse(mask[60:].any())

    def test_exit_candidates_are_excluded_and_persistent_exit_resets_state(self) -> None:
        values = self.rows(140)
        self.set_firing(values, 0, 60)
        self.set_firing(values, 70, 130)
        valve_running = np.ones(len(values), dtype=bool)
        mask = effective_burning_mask(values, valve_running, self.config)

        self.assertTrue(mask[:60].all())
        self.assertFalse(mask[60:70].any())
        self.assertTrue(mask[70:130].all())
        self.assertFalse(mask[130:].any())

    def test_short_exit_candidate_breaks_windows_but_does_not_reset_state(self) -> None:
        values = self.rows(90)
        self.set_firing(values, 0, 90)
        values[60:65, INPUT_FIELDS.index("natural_gas_inlet_flow_m3_h")] = 10.0
        valve_running = np.ones(len(values), dtype=bool)
        mask = effective_burning_mask(values, valve_running, self.config)

        self.assertTrue(mask[:60].all())
        self.assertFalse(mask[60:65].any())
        self.assertTrue(mask[65:].all())

    def test_valve_stop_and_invalid_operational_signal_reset_immediately(self) -> None:
        values = self.rows(140)
        self.set_firing(values, 0, 140)
        valve_running = np.ones(len(values), dtype=bool)
        valve_running[70] = False
        values[100, INPUT_FIELDS.index("blower_current_a")] = np.nan

        mask = effective_burning_mask(values, valve_running, self.config)

        self.assertTrue(mask[:60].all())
        self.assertFalse(mask[70:130].any())
        self.assertFalse(mask[100:].any())


if __name__ == "__main__":
    unittest.main()
