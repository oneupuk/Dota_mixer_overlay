import { AbsoluteFill, Img, Interactive, Still, staticFile } from "remotion";
import { inter } from "./fonts";

// docs/controls.png - the widget at 2x with what each part does.
// widget.png is a 528x116 crop of the 2560x1440 capture; it is drawn at 2x from (WX, WY).
const WX = 272;
const WY = 236;

type CalloutProps = {
  readonly name: string;
  readonly targetX: number; // in widget.png pixels
  readonly targetY: number;
  readonly labelX: number; // label anchor on the canvas (center of the label's near edge)
  readonly labelY: number;
  readonly title: string;
  readonly detail: string;
  readonly above: boolean;
};

const Callout: React.FC<CalloutProps> = ({ name, targetX, targetY, labelX, labelY, title, detail, above }) => {
  const tx = WX + targetX * 2;
  const ty = WY + targetY * 2;
  return (
    <>
      <svg width={1600} height={700} style={{ position: "absolute", left: 0, top: 0 }}>
        <line x1={tx} y1={ty} x2={labelX} y2={labelY} stroke="rgba(255,196,140,0.75)" strokeWidth={2} />
        <circle cx={tx} cy={ty} r={7} fill="#ffb057" stroke="#0b0d15" strokeWidth={3} />
      </svg>
      <Interactive.Div
        name={name}
        style={{
          position: "absolute",
          left: labelX - 190,
          top: above ? labelY - 92 : labelY,
          width: 380,
          height: 92,
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: above ? "flex-end" : "flex-start",
          paddingTop: above ? 0 : 12,
          paddingBottom: above ? 12 : 0,
          textAlign: "center",
        }}
      >
        <div style={{ color: "#f4f6fb", fontSize: 26, fontWeight: 700, letterSpacing: -0.3 }}>{title}</div>
        <div style={{ marginTop: 4, color: "#a9b1c4", fontSize: 19 }}>{detail}</div>
      </Interactive.Div>
    </>
  );
};

export const Controls: React.FC = () => {
  return (
    <AbsoluteFill
      name="Background"
      style={{
        background:
          "radial-gradient(ellipse 55% 60% at 50% 45%, rgba(255,140,70,0.13), rgba(0,0,0,0) 70%), linear-gradient(180deg, #121624 0%, #0a0c14 100%)",
        fontFamily: inter,
      }}
    >
      <Img
        name="Widget 2x"
        src={staticFile("widget.png")}
        style={{
          position: "absolute",
          left: WX,
          top: WY,
          width: 1056,
          height: 232,
          borderRadius: 28,
          boxShadow: "0 30px 80px rgba(0,0,0,0.6)",
          border: "1px solid rgba(255,255,255,0.08)",
        }}
      />

      <Callout
        name="Callout: track"
        targetX={110}
        targetY={36}
        labelX={420}
        labelY={150}
        title="What's playing"
        detail="Cover, title and artist from any app"
        above
      />
      <Callout
        name="Callout: buttons"
        targetX={425}
        targetY={32}
        labelX={1130}
        labelY={150}
        title="Previous · Play/Pause · Next"
        detail="Dimmed when the player can't do it"
        above
      />
      <Callout
        name="Callout: seek"
        targetX={234}
        targetY={82}
        labelX={560}
        labelY={556}
        title="Seek"
        detail="Click or drag the bar"
        above={false}
      />
      <Callout
        name="Callout: volume"
        targetX={436}
        targetY={82}
        labelX={1100}
        labelY={556}
        title="App volume"
        detail="Drag, or scroll anywhere on the widget"
        above={false}
      />
    </AbsoluteFill>
  );
};

export const ControlsStill: React.FC = () => {
  return <Still id="Controls" component={Controls} width={1600} height={700} />;
};
