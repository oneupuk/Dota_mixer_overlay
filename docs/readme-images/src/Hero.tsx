import { AbsoluteFill, Img, Still, staticFile } from "remotion";

// docs/hero.jpg - what the overlay looks like in a real game (Demo Hero at 2560x1440, middle of the screen).
export const Hero: React.FC = () => {
  return (
    <AbsoluteFill
      name="Background"
      style={{
        background:
          "radial-gradient(ellipse 70% 55% at 50% 0%, rgba(255,140,70,0.20), rgba(0,0,0,0) 70%), linear-gradient(180deg, #121624 0%, #0a0c14 100%)",
      }}
    >
      <Img
        name="Game screenshot"
        src={staticFile("game-open.jpg")}
        style={{
          position: "absolute",
          left: 80,
          top: 56,
          width: 1440,
          height: 810,
          borderRadius: 22,
          border: "1px solid rgba(255,255,255,0.10)",
          boxShadow: "0 40px 90px rgba(0,0,0,0.65)",
        }}
      />
    </AbsoluteFill>
  );
};

export const HeroStill: React.FC = () => {
  return <Still id="Hero" component={Hero} width={1600} height={922} />;
};
