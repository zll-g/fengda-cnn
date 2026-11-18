"""CNN-LSTM-Attention model skeleton with Dropout."""

from __future__ import annotations

from dataclasses import dataclass

try:
    import torch
    from torch import nn
except ImportError:  # pragma: no cover - local environment currently has no torch installed
    torch = None
    nn = None


@dataclass
class CNNLSTMAttentionConfig:
    input_size: int
    output_steps: int = 60
    output_size: int = 5
    conv_channels_1: int = 64
    conv_channels_2: int = 128
    kernel_size: int = 3
    lstm_hidden_size: int = 128
    dense_hidden_size: int = 256
    conv_dropout: float = 0.2
    lstm_dropout: float = 0.2
    dense_dropout: float = 0.3


if nn is not None:

    class AdditiveAttention(nn.Module):
        """Time-wise attention over the LSTM output sequence."""

        def __init__(self, hidden_size: int) -> None:
            super().__init__()
            self.projection = nn.Linear(hidden_size, hidden_size)
            self.score = nn.Linear(hidden_size, 1, bias=False)

        def forward(self, sequence_output: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
            attention_scores = self.score(torch.tanh(self.projection(sequence_output))).squeeze(-1)
            attention_weights = torch.softmax(attention_scores, dim=1)
            context = torch.sum(sequence_output * attention_weights.unsqueeze(-1), dim=1)
            return context, attention_weights


    class CNNLSTMAttentionModel(nn.Module):
        """Forecast future multi-pollutant trajectories from a historical feature window."""

        def __init__(self, config: CNNLSTMAttentionConfig) -> None:
            super().__init__()
            self.config = config

            self.conv1 = nn.Conv1d(
                in_channels=config.input_size,
                out_channels=config.conv_channels_1,
                kernel_size=config.kernel_size,
                padding="same",
            )
            self.conv1_dropout = nn.Dropout(config.conv_dropout)

            self.conv2 = nn.Conv1d(
                in_channels=config.conv_channels_1,
                out_channels=config.conv_channels_2,
                kernel_size=config.kernel_size,
                padding="same",
            )
            self.conv2_dropout = nn.Dropout(config.conv_dropout)

            self.lstm = nn.LSTM(
                input_size=config.conv_channels_2,
                hidden_size=config.lstm_hidden_size,
                batch_first=True,
            )
            self.lstm_dropout = nn.Dropout(config.lstm_dropout)
            self.attention = AdditiveAttention(config.lstm_hidden_size)

            self.head = nn.Sequential(
                nn.Linear(config.lstm_hidden_size, config.dense_hidden_size),
                nn.ReLU(),
                nn.Dropout(config.dense_dropout),
                nn.Linear(
                    config.dense_hidden_size,
                    config.output_steps * config.output_size,
                ),
            )

        def forward(self, inputs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
            # inputs: [batch, steps, features]
            x = inputs.transpose(1, 2)
            x = torch.relu(self.conv1(x))
            x = self.conv1_dropout(x)
            x = torch.relu(self.conv2(x))
            x = self.conv2_dropout(x)

            x = x.transpose(1, 2)
            x, _ = self.lstm(x)
            x = self.lstm_dropout(x)

            context, attention_weights = self.attention(x)
            forecast = self.head(context).view(
                inputs.size(0),
                self.config.output_steps,
                self.config.output_size,
            )
            return forecast, attention_weights

else:

    class CNNLSTMAttentionModel:  # pragma: no cover - fallback for no-torch environments
        """Fallback that raises a clear error when torch is unavailable."""

        def __init__(self, config: CNNLSTMAttentionConfig) -> None:
            del config
            raise ImportError(
                "PyTorch is not installed in the current environment. "
                "Install the optional dependency group `train` to use the model module."
            )
