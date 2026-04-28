use anyhow::{Context, Result, bail};
use std::env;
use std::path::{Path, PathBuf};
use transcribe_rs::SpeechModel;
use transcribe_rs::TranscribeOptions;
use transcribe_rs::onnx::Quantization;
use transcribe_rs::onnx::canary::CanaryModel;
use transcribe_rs::onnx::gigaam::GigaAMModel;
use transcribe_rs::onnx::moonshine::{MoonshineModel, MoonshineVariant, StreamingModel};
use transcribe_rs::onnx::parakeet::ParakeetModel;
use transcribe_rs::onnx::sense_voice::SenseVoiceModel;

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut args = env::args().skip(1);
    let model_id = args.next().context("Missing model id argument.")?;
    let model_path = PathBuf::from(args.next().context("Missing model path argument.")?);
    let audio_path = PathBuf::from(args.next().context("Missing audio path argument.")?);

    if !model_path.exists() {
        bail!("Model path was not found: {}", model_path.display());
    }

    if !audio_path.exists() {
        bail!("Audio file was not found: {}", audio_path.display());
    }

    let text = transcribe(&model_id, &model_path, &audio_path)?;
    print!("{}", text.trim());
    Ok(())
}

fn transcribe(model_id: &str, model_path: &Path, audio_path: &Path) -> Result<String> {
    let options = TranscribeOptions::default();

    let text = match model_id {
        "parakeet-tdt-0.6b-v2" | "parakeet-tdt-0.6b-v3" => {
            let mut model = ParakeetModel::load(&model_path.to_path_buf(), &Quantization::Int8)
                .context("Failed to load Parakeet model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with Parakeet.")?
                .text
        }
        "canary-180m-flash" | "canary-1b-v2" => {
            let mut model = CanaryModel::load(&model_path.to_path_buf(), &Quantization::Int8)
                .context("Failed to load Canary model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with Canary.")?
                .text
        }
        "sense-voice-int8" => {
            let mut model = SenseVoiceModel::load(&model_path.to_path_buf(), &Quantization::Int8)
                .context("Failed to load SenseVoice model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with SenseVoice.")?
                .text
        }
        "moonshine-base" => {
            let mut model = MoonshineModel::load(
                &model_path.to_path_buf(),
                MoonshineVariant::Base,
                &Quantization::default(),
            )
            .context("Failed to load Moonshine Base model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with Moonshine Base.")?
                .text
        }
        "moonshine-tiny-streaming-en"
        | "moonshine-small-streaming-en"
        | "moonshine-medium-streaming-en" => {
            let mut model = StreamingModel::load(&model_path.to_path_buf(), 4, &Quantization::default())
                .context("Failed to load Moonshine streaming model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with Moonshine streaming.")?
                .text
        }
        "gigaam-v3-e2e-ctc" => {
            let mut model = GigaAMModel::load(&model_path.to_path_buf(), &Quantization::Int8)
                .context("Failed to load GigaAM model.")?;
            model
                .transcribe_file(&audio_path.to_path_buf(), &options)
                .context("Failed to transcribe with GigaAM.")?
                .text
        }
        _ => bail!("Unsupported model id: {model_id}"),
    };

    Ok(text)
}
