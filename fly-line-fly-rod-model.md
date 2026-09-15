
# Numerical model for the dynamics of a coupled fly line/fly rod system and experimental validation

**Caroline Gatti-Bono**<sup>a</sup>, **N. C. Perkins**<sup>b,*</sup>

<sup>a</sup> Applied Numerical Algorithms, CRD, Lawrence Berkeley National Laboratory, Berkeley, CA 94720, USA  
<sup>b</sup> Department of Mechanical Engineering, University of Michigan, 2250 G.G. Brown, Ann Arbor, MI 48109-2125, USA

*Journal of Sound and Vibration* 272 (2004), 773-791  
Received 26 September 2002; accepted 25 March 2003  
DOI: [10.1016/S0022-460X(03)00419-X](https://doi.org/10.1016/S0022-460X(03)00419-X)

## Abstract

A model is presented that describes the two-dimensional dynamics of the coupled system composed of a
fly rod and a fly line. The fly line is modelled as a long elastica that is subjected to tension, bending,
aerodynamic drag, and weight. The fly rod is modelled as a flexible Euler-Bernoulli beam possessing two
degrees of freedom: a rigid-body mode, and a flexible-body mode. The line and the rod are coupled through
the boundary conditions at their interface. The resulting initial two-point boundary-value problem is solved
numerically using a finite difference algorithm. The numerical solutions for an overhead cast are computed
using experimental data as the input for the motion of caster's hand. The shape of the fly line and the
position of the fly rod are compared with images captured by video.
© 2003 Elsevier Ltd. All rights reserved.

## 1. Introduction

In fly fishing, the angler casts a long fly line to present an artificial “fly” to a feeding fish.
Considerable practice and a good understanding of the mechanics of the fly line and fly rod are
emphasized in fly casting instruction [1-3]. Consider a standard overhead cast as illustrated in
Fig. 1. These idealized sketches illustrate four stages of the forward cast portion of an overhead
cast that starts with the fly line laid out horizontally behind the caster at the conclusion of a back
cast; see Fig. 1(a). The caster then rotates the fly rod clockwise, accelerates the fly line, and then
abruptly stops the rod as illustrated in Fig. 1(b). From this point onwards, the end of the fly line
attached to the rod tip remains stationary and a loop of line necessarily forms between the moving
(upper) portion of the fly line and the stationary (lower) portion of the fly line; see Fig. 1(b). From

<sup>*</sup>Corresponding author. Tel.: +1-734-936-0403; fax: +1-734-615-6647. Email: ncp@umich.edu.

(a)

(b)

(c)

(d)

> **Figure 1. The forward cast of an overhead cast: (a) perfectly laid out back cast; (b) loop formation just after stop of**
forward cast; (c) loop propagation; (d) completion of cast and loop turnover.

the perspective of dynamics, this loop represents a nonlinear wave that propagates forward
as shown in Fig. 1(c) until it reaches the end of the fly line where the loop turns over as shown in
Fig. 1(d). This final turnover occurs on or near the surface of the water and the forward cast is
complete.
Fly rod and fly line manufacturers have utilized decades (if not centuries) of experience in
designing their products during which they have taken full advantage of the new engineering
materials introduced from other industries [4]. However, as emphasized in Ref. [4], equipment
manufacturers have yet to take full advantage of computer simulation technology as one means to
assess and explore new designs. A first step towards this goal is the numerical model of the fly line
alone as described in Ref. [5]. A second step is the numerical model of a fully coupled fly line and
fly rod system as presented herein.
Spolek references most of the literature published on fly casting on a website [6]. A brief
overview of the papers most closely related to this work follows and a more detailed review can be
found in Ref. [5]. Spolek [7] and Lingard [8] developed analytical models that describe the
propagation of an idealized loop. They prescribe the geometry (semi-circular) and the initial
velocity of the loop and then use a work-energy balance to compute the velocity of the fly as a
function of position during the forward cast. Subsequently, Robson [9] contributed a numerical
model of the fly line that relaxes the assumptions of idealized loop geometry. The discretization
employs a lumped parameter representation which then introduces numerous assumed parameters
including the length and mass of discrete elements, frictionless joints, etc. Major restrictions of
this model, however, are that it does not account for the fly line taper (non-uniform diameter) and
bending stiffness. Fly lines are intentionally tapered to improve casting performance; and bending
rigidity, while rather small, is essential in the regions of low tension and compression that develop
on the lower part of the loop.


A continuum model for the fly line was introduced in Refs. [5,10] using advances in the
literature on cable dynamics. In Ref. [5], a fly line with prescribed taper is modelled as a long non-
uniform elastica that is allowed to undergo arbitrarily large rotations. This model accounts for
tension, bending, aerodynamic drag, and weight, and the formulation begins with the physical
parameters defining a fly line including fly line density, taper, and bending stiffness. A numerical
algorithm is presented to solve the resulting initial two-point boundary-value problem, and
several examples are discussed that highlight fundamental features of fly line dynamics. This
model, however, requires a priori knowledge of the path (and velocity) followed by the tip of the
fly rod. Moreover, it does not account for the coupled dynamics of the fly line and the fly rod.
Robson [9] includes the fly rod in his formulation but prescribes its motion in realizing an
uncoupled model. He begins with a rigid fly rod, hence the rod tip path is circular. He then adds
rod bending using an empirical relation for the bending of a pre-determined (top) portion of the
fly rod in a circular arc. Spolek considers only the fly rod in Ref. [11] and determines
experimentally that the stiffness and the (fundamental) natural frequency of the fly rod provide a
quantitative description of the rod's casting action. Building on this idea, Hoffmann and Hooper
[12] develop a finite-element model that predicts the stiffness and the natural frequency of the fly
rod depending on its design. No studies of fly rod dynamics have drawn from the mathematical
formulations of rotating beams. For example, Yigit et al. [13] consider an Euler-Bernoulli beam
attached to a rotating hub, and then employ a Galerkin expansion to compute the vibration
waveforms.
The objective of this paper is to establish a numerical model that couples the dynamics of the fly
rod to the dynamics of the fly line. The model for the fly line is identical to that presented in
Ref. [5] and takes the form of a very long and non-uniform (tapered) elastica. The fly rod is
modelled as a rotating Euler-Bernoulli beam with prescribed rotation of a rigid-body mode and
additional dynamics due to the fundamental mode of rod vibration. These fly line and fly rod
models are summarized in Section 2. After a brief overview of the numerical algorithm in
Section 3, and a description of an experiment in Section 4, we review and compare experimental
and numerical results for overhead casts in Section 5.

## 2. Continuum models of fly line and fly rod

In this section, we review the two-dimensional equations of motion for a fly line subject to
tension, bending, aerodynamic drag and weight. This is followed by a simple dynamic model of a
fly rod as a rotating Euler-Bernoulli beam. We then define the interface conditions that couple the
fly line and fly rod models.

### 2.1. Fly line model

We will briefly summarize the steps that yield the equations of planar motion for the fly line as
developed in Ref. [5].
First, we introduce two reference frames depicted in Fig. 2. The inertial reference frame
(O; e1 ; e2 ; e3 ) is used to describe vector quantities including the acceleration, and weight of an
infinitesimal segment of the fly line; refer to Fig. 3. The local reference frame (M; a1 ; a2 ; a3 ) is used

> **Figure 2. Definition of the inertial and local reference frames.**

> **Figure 3. Definition of the forces and moments acting on an infinitesimal segment of fly line.**

to describe the local curvature, the tension, the shear and the drag forces. Here, a1 is the unit
normal vector, a2 is the unit bi-normal vector, and a3 is the unit tangent vector. The
transformation between the two reference frames is a function of the Euler angle f shown in
Fig. 2, and can be written as follows. Let (z1 ; z2 ; z3 ) be the components of a vector z in the local
reference frame, and let (Z1 ; Z2 ; Z3 ) be the components of z in the inertial reference frame. These
components are related through

$$
\begin{Bmatrix}z_1\\z_2\\z_3\end{Bmatrix}=
\begin{bmatrix}\cos\phi&\sin\phi&0\\0&0&1\\\sin\phi&-\cos\phi&0\end{bmatrix}
\begin{Bmatrix}Z_1\\Z_2\\Z_3\end{Bmatrix}.
\tag{1}
$$

The equations of motion of the fly line can then be written [5]

$$\frac{\partial v_1}{\partial s}=-\kappa v_3+\frac{\partial\phi}{\partial t},\qquad \frac{\partial v_3}{\partial s}=\kappa v_1,\qquad \frac{\partial\phi}{\partial s}=\kappa.\tag{2-4}$$

$$\frac{\partial\kappa}{\partial s}=\frac{1}{EJ}\left(-f_1-\frac{\pi ED^3}{16}\kappa+\frac{\partial(\rho_lJ\,\partial\phi/\partial t)}{\partial t}\right).\tag{5}$$

$$\frac{\partial f_1}{\partial s}=-\kappa f_3-F_1+\rho_lA(s)\left(\frac{\partial v_1}{\partial t}+\frac{\partial\phi}{\partial t}v_3\right).\tag{6}$$

$$\frac{\partial f_3}{\partial s}=\kappa f_1-F_3+\rho_lA(s)\left(\frac{\partial v_3}{\partial t}-\frac{\partial\phi}{\partial t}v_1\right).\tag{7}$$

in terms of the six unknowns (v1 ; v3 ; f; k; f1 ; f3 ): The independent variables include the spatial
co-ordinate s that measures the arc length along the fly line, and time t: The unknowns include the
two components of the planar velocity vector v = v1 a1 + v3 a3 ; the Euler angle f; the fly line


curvature k; and the fly line shear f1 and tension f3 : The fly line material and geometric parameters
include Young's modulus E; density rl ; area moment of inertia J = pD4 (s)=64; and diameter D(s):
Note that a specific line taper can be accounted for by prescribing the variation of the diameter D
with position s:
The first three equations (2)-(4) define an inextensibility constraint, the fly line angular velocity,
and curvature, respectively. The remaining three equations (5)-(7) represent the angular
momentum and the (two) linear momentum equations. The momentum equations follow from
the Newton/Euler equations of an infinitesimal segment of fly line depicted in Fig. 3. These
momentum equations will now be completed by defining the external forces per unit length F1 and
F3 acting on the fly line by accounting for aerodynamic drag and self-weight.
A standard (Morison) drag formulation is adopted. Let Cd1 and Cd3 denote drag coefficients
associated with tangential drag (skin friction) and normal drag, respectively. Then, the drag per
unit length

$$\mathbf h=h_1\mathbf a_1+h_3\mathbf a_3.\tag{8}$$

has components

$$h_1=-\frac12\rho_aD(s)\pi C_{d1}v_1|v_1|,\qquad h_3=-\frac12\rho_aD(s)C_{d3}v_3|v_3|.\tag{9,10}$$

where $\rho_a$ is the density of air. Adding now the tangential and normal components of the weight of
the fly line per unit length leads to

$$F_1=h_1+\rho_lgA(s)\sin\phi,\qquad F_3=h_3+\rho_lgA(s)\cos\phi.\tag{11,12}$$

### 2.2. Fly rod model

In this section, we develop an approximate model that captures the flexible and rigid-body
motions of the fly rod. One component of the rigid-body motion of the fly rod is the translation of
the bottom (grip) of the fly rod during casting. However, the contribution of this translation to the
velocity of the fly rod tip is very small compared to the contribution due to rod rotation.1 We use
this observation in the following model where we prescribe the motion for the rigid-body mode for
rotation as the (sole) input from the fly caster. The acceleration due to this prescribed rotation and
the tension due to the fly line “load” the rod, creating bending during casting. We account for the
dynamics of fly rod bending by modelling the fundamental mode of bending vibration of the fly
rod. We shall approximate this bending using linear Euler-Bernoulli beam theory. It should be
noted however, that our approach could readily be extended to include large (non-linear) flexible-
body rotations of the fly rod, and that this might become important when the rod is “loaded”
significantly during distance casting.

#### 2.2.1. Equations of motion of the fly rod
The hollow tapered fly rod is approximated by a hollow rod of uniform outer diameter do and
of uniform inner diameter di : The taper could also be accounted for as in Ref. [12], and this would
1
The motion of the tip of the fly rod is the input to the fly line and therefore determines the dynamics of loop
formation and loop propagation. Substantial instruction in fly casting centers on the fundamental role played by the
rod “tip path” [1-3].

> **Figure 4. Schematic of the fly rod in an undeflected position (“shadow beam”) and in a deflected position.**

be very important in using the rod/line model for the purpose of a specific rod design. As
mentioned above, the fly rod is modelled as an Euler-Bernoulli beam with prescribed rotation of a
rigid-body mode. The orientation of this rigid-body mode is given by the prescribed angle fr (t);
and this also defines a “shadow beam” as illustrated in Fig. 4.
The details of the derivation of the equations of motion for the fly rod can be found in Ref. [15],
and the final results are reported below:

$$E_rA_ru_{,xx}(x,t)=0.\tag{13}$$

$$-\rho_rA_r\ddot w(x,t)+\rho_rA_r\dot\phi_r^{2}(t)w(x,t)-\rho_rA_rx\ddot\phi_r(t)-E_rI_rw_{,xxxx}(x,t)=0.\tag{14}$$

$$u(0,t)=0,\qquad w(0,t)=0,\qquad w_{,x}(0,t)=0.\tag{15-17}$$

$$-E_rA_ru_{,x}(l_r,t)+T^r(t)=0,\qquad w_{,xx}(l_r,t)=0,\qquad E_rI_rw_{,xxx}(l_r,t)+V^r(t)=0.\tag{18-20}$$

Here, u and w are the longitudinal and lateral displacements of the rod centerline as defined in
Fig. 5, and x is an independent spatial co-ordinate measured along the shadow beam. The rod
material properties are represented by Young's modulus Er and density rr : The rod geometric
(averaged) properties are represented by the area Ar and the second area moment Ir : The tension
and shear at the tip of the rod are denoted by T r and V r ; respectively. Eq. (13) represents the
equation of motion in the longitudinal direction where quasi-static stretching has been assumed.
Eq. (14) represents the equation of motion in the transverse direction. Eqs. (15)-(17) represent the
boundary conditions at the caster's hand. Eqs. (18)-(20) represent the boundary conditions at the
fly rod/fly line junction.
The longitudinal displacement can be readily obtained by integrating Eq. (13) along with
boundary conditions (15) and (18):

$$u(x,t)=\frac{T^r(t)}{E_rA_r}x.\tag{21}$$

> **Figure 5. Definition of the longitudinal (u) and lateral (w) displacements.**

The lateral motion of the fly rod, described by the system of equations (14), (16)-(17), and
(19)-(20), can be recast in the form

$$\rho_rA_rv_{,tt}-\rho_rA_r\dot\phi_r^{2}v+E_rI_rv_{,xxxx}=-\rho_rA_rx\ddot\phi_r-\rho_rA_r\dot\phi_r^{2}\frac{V^rx^2(x-3l_r)}{6E_rI_r}+\rho_rA_r\frac{\ddot V^rx^2(x-3l_r)}{6E_rI_r}.\tag{22}$$

$$v(0,t)=0,\quad v_{,x}(0,t)=0,\quad v_{,xx}(l_r,t)=0,\quad v_{,xxx}(l_r,t)=0.\tag{23-26}$$

where $v(x,t)=w(x,t)-V^r(t)x^2(x-3l_r)/(6E_rI_r)$ now satisfies the (homogeneous) boundary conditions of a clamped-free beam.

#### 2.2.2. Galerkin expansion
The lateral motion of the fly rod is now approximated by a Galerkin expansion using the first
mode shape of a clamped-free beam. The use of a single mode is well justified in this application
as seen in photographic images of a fly rod during casting; see, for example, Ref. [14].
The first mode shape is given by

$$\tilde v_1(x)=[\cos(\lambda_1l_r)+\cosh(\lambda_1l_r)][\sin(\lambda_1x)-\sinh(\lambda_1x)]-[\sin(\lambda_1l_r)+\sinh(\lambda_1l_r)][\cos(\lambda_1x)-\cosh(\lambda_1x)].\tag{27}$$

where $\lambda_1=1.875/l_r$ [15].

The transverse displacement of the fly rod is sought in the form

$$v(x,t)=\mu_1(t)\tilde v_1(x).\tag{28}$$

Substitution into Eq. (22) and application of the Galerkin method yields the ordinary differential
equation for the modal co-ordinate $\mu_1(t)$:

$$[\ddot\mu_1+(\omega_1^2-\dot\phi_r^{2})\mu_1]\int_0^{l_r}\tilde v_1^2\,dx=-\ddot\phi_r\int_0^{l_r}x\tilde v_1\,dx+[-V^r\dot\phi_r^{2}+\ddot V^r]\int_0^{l_r}\frac{x^2(x-3l_r)}{6E_rI_r}\tilde v_1\,dx.\tag{29}$$

### 2.3. Fly rod and fly line coupling

The interface conditions that couple the dynamics of the fly line and fly rod models are
developed. We assume that the line and the rod are pinned-connected at the rod tip.
A force balance at the rod tip/fly line junction gives

$$T^r=-[f_1\cos\phi+f_3\sin\phi]\sin(\phi_r+\theta^r)+[f_1\sin\phi-f_3\cos\phi]\cos(\phi_r+\theta^r).\tag{30}$$

$$V^r=-[f_1\cos\phi+f_3\sin\phi]\cos(\phi_r+\theta^r)-[f_1\sin\phi-f_3\cos\phi]\sin(\phi_r+\theta^r),\tag{31}$$

where all fly-line quantities in (30)-(31) are evaluated at $(0,t)$, and where $\phi(0,t)$ is the (Euler) angle formed between the vertical and the normal to the fly line at the
junction, yr (t) is the angle formed between the shadow beam and the rod tangent at the junction,
f1 (0; t) is the fly line shear force, and f3 (0; t) is the fly line tension at the junction.
Continuity of velocity (i.e., from continuity of displacement) at the junction yields

$$v_1(0,t)\cos\phi(0,t)+v_3(0,t)\sin\phi(0,t)=\dot X_1^r(t).\tag{32}$$

$$v_1(0,t)\sin\phi(0,t)-v_3(0,t)\cos\phi(0,t)=\dot X_2^r(t).\tag{33}$$

where v1 (0; t) and v3 (0; t) are the normal and tangential components of the velocity of the fly line at
the junction, respectively, and X' r1 (t) and X' r2 (t) are the vertical and horizontal components of the
velocity of the fly rod at the junction, respectively.

### 2.4. Initial and boundary conditions

The model is now completed by prescribing the initial and the boundary conditions.

#### 2.4.1. Initial conditions
The fly rod is assumed to be initially straight and inclined at an angle of $\phi_r(0)=\pi/4$ while the
fly line is laid out horizontally, at rest, at the end of a (perfect) back cast.

#### 2.4.2. Boundary condition: end of fly line
The tip (end) of the fly line (or leader) may be considered free or subject to the effect of an
attached fly. In Ref. [5], we discuss the influence of the mass and drag of an attached fly. Here, we
shall pick the simpler case of a free end (no fly) and we shall follow this by a companion


experiment on casting without a fly. The relevant boundary conditions are

$$\kappa(l,t)=0,\qquad f_1(l,t)=0,\qquad f_3(l,t)=0.\tag{34-36}$$

#### 2.4.3. Boundary condition: caster's hand
The fly rod and line chosen for the experiment is a small “yarn rod” that is easy to instrument
and use in an indoor laboratory; refer to Section 4. During casting, this small system is cast using
predominantly rotation of the caster's wrist. Thus, we shall assume that end of the fly rod is a
fixed pivot and we prescribe the (rigid-body) rotation fr (t): The resulting vertical and horizontal
positions of the rod tip are expressed by

$$X_1^r=-l_r\sin\phi_r-\left[\mu_1\tilde v_1(l_r)+\frac{V^rl_r^3}{3E_rI_r}\right]\cos\phi_r.\tag{37}$$

$$X_2^r=l_r\cos\phi_r-\left[\mu_1\tilde v_1(l_r)+\frac{V^rl_r^3}{3E_rI_r}\right]\sin\phi_r.\tag{38}$$

All quantities in (37)-(38) are functions of $t$, except $l_r$, $E_r$, $I_r$, and $\tilde v_1(l_r)$, which capture both the rigid and flexible motions of the fly rod; refer to Ref. [15].

#### 2.4.4. Intermediate condition at junction
In this section, we transform our current two-point boundary-value problem with an
intermediate condition into a two-point boundary-value problem for the fly line only. In this
step, we find 3 equations, among the 6 equations describing the intermediate condition at the
junction, that involve only known quantities and the unknown variables (v1 ; v3 ; f; k; f1 ; f3 ) for
the fly line at s = 0: Consider the continuity of velocity and the zero moment condition at the
junction:

$$v_1(0,t)\cos\phi(0,t)+v_3(0,t)\sin\phi(0,t)-\dot X_1^r(t)=0.\tag{39}$$

$$v_1(0,t)\sin\phi(0,t)-v_3(0,t)\cos\phi(0,t)-\dot X_2^r(t)=0,\qquad \kappa(0,t)=0.\tag{40,41}$$

We will now use previous results to relate X' r1 (t) and X' r2 (t) to the 6 unknowns (v1 ; v3 ; f; k; f1 ; f3 )
for the fly line at s = 0: Differentiating Eqs. (37) and (38), we can express the velocity components
of the rod tip as functions of m1 (t); V r (t); and their derivatives. Eq. (29) relates m1 (t) to V r (t); and
Eqs. (30) and (31) express the quantities V r (t) and T r (t) as functions of the unknowns f; f1 ; and f3
of the fly line at s = 0:

## 3. Comment on numerical algorithm

The non-linear initial-boundary-value problem above is solved using space-time finite
differencing. The algorithm is a modification of that developed in Refs. [16,17]. The key steps
in the algorithm are as follows:
- Transform the space-time problem (2)-(7) into a spatial two-point boundary-value problem
using finite differencing in time.

- Approximate the resulting non-linear differential equations as a system of linear differential
equations using a first order Taylor series expansion to obtain a linear two-point boundary-
value problem.
- Transform the linear two-point boundary-value problem into a linear initial-value problem

which can then be solved efficiently and then iterate for non-linear corrections by updating the
Taylor series expansion above.

These steps are detailed in Ref. [15].

## 4. Experiment

This experiment uses a “yarn rod” that is formed by attaching a short (2:5 m) length of yarn to
the upper half of a fly rod. Yarn rods are sometimes used by fly casting instructors as a teaching
tool because they demonstrate similar dynamic characteristics as a full size fly rod, but their small
size makes them easier to use indoors, and particularly so in the laboratory where this experiment
was conducted. The small size also aids the experimental measurement of rod and line dynamics
as both can be viewed using standard video equipment. Moreover, the motion of the yarn rod is
slower than that of a full scale fly rod, and this also renders it amenable to capturing on standard
VHS video.
A schematic of the experimental setup is provided in Fig. 6. The yarn rod is cast adjacent to a
contrasting background, and the dynamics of the line (yarn) are recorded using standard video.
The positions of the ends of the fly rod are measured using an “Optotrack'' camera system. This
device is an array of three infrared cameras, that uses the principle of triangulation to track the
position of infrared light-emitting diode “targets'' to within 1 mm accuracy in three dimensions.
To track the motion of the two ends of the fly rod, we attached two small (less than 1 cm
diameter) targets to the top and the bottom of the fly rod. The targets were first mounted on foam

Background
Yarnrod

Fly caster

Optotrack
camera system    Video system           Data acquisition

> **Figure 6. Experimental setup used to record the positions of the rod ends and the fly line shape as functions of time.**


blocks that provided flat mounting surfaces. The selected sampling frequency of 120 Hz provided
ample resolution of rod dynamics.

## 5. Results

In this section, we will first use the fly line model presented in Ref. [5] to establish that the
dynamics of a length of yarn cast with a small rod shares the same fundamental characteristics as
fly line cast with a full-length fly rod. Then, we will analyze results obtained for the coupled fly
line/fly rod model presented herein and compare them with the uncoupled model. Finally, we will
compare the calculated results for the coupled model with the images of casting captured by video.

### 5.1. Uncoupled model

We begin with the simpler model of a fly line alone and review the fundamental characteristics
of loop formation and propagation. The fly line model requires as input the velocity of the tip of
the fly rod and this input can be computed from the target data for the diode placed at the tip of
the fly rod. The parameters used to perform the simulation are listed in Table 1.
Fig. 7 shows the predicted shape of the yarn during a portion of the forward cast for 6 selected
times. The loop formation and propagation show the same basic characteristics as noted for “full-
scale'' fly casting [5], including three major phases. The first phase is characterized by a very rapid
but smooth increase in fly line speed with little to no flexible body deformation of the fly line. The
abrupt stop of the rod tip initiates the second phase that is characterized by a dramatic drop in
the tension and speed of the fly line at the rod tip. The resulting velocity difference between the
extreme ends of the fly line generates rapid rotations (flexible body deformation) of the line during
the creation of a loop. During the third phase, this loop propagates along the fly line under the
influence of line tension and air drag while falling slightly under the influence of gravity. In this
example, the first phase lasts from t = 0 to 0:45 s; and the line assumes the shape of the curved
path created by the rod tip. During this phase, the line also accelerates and reaches a peak

**Table 1. Data used for simulation of uncoupled line model of Section 5.1**

| Parameter | Symbol | Value |
|---|---:|---:|
| Bending stiffness of the line | $E$ | $0.5\times10^9\,\mathrm{N/m^2}$ |
| Length of the line | $l$ | $2.13\,\mathrm m$ |
| Diameter of the line | $d$ | $0.003\,\mathrm m$ |
| Density of the line | $\rho_l$ | $100\,\mathrm{kg/m^3}$ |
| Density of air | $\rho_a$ | $1.29\,\mathrm{kg/m^3}$ |
| Normal drag coefficient | $C_{d1}$ | $1$ |
| Tangential drag coefficient | $C_{d3}$ | $0.01$ |
| Coarse time step | $\Delta t_1$ | $0.005\,\mathrm s$ |
| Fine time step | $\Delta t_2$ | $0.005\,\mathrm s$ |
| Spatial step | $\Delta s$ | $0.0028\,\mathrm m$ |
| Prescribed error | $\Delta e$ | $0.05$ |

1.2

1

0.8

0.6
Rod tip path
0.4
Vertical position (m)

0.2

0

-0.2

-0.4

-0.6

-0.8

-2.5               -2           -1.5                 -1            -0.5                 0
Horizontal position (m)

> **Figure 7. Numerical prediction of the forward cast when the rod tip path, obtained experimentally, is prescribed: —,**
shape of line; - - ; shape of rod tip path.

15

10
Vertical velocity (m/s)

5

0

-5

-10
(a)                                            0          0.2   0.4        0.6   0.8        1        1.2     1.4       1.6        1.8          2
Time (s)
Phase 1            Phase 2
Phase 3
15
Horizontal velocity (m/s)

10

5

0

-5

-10

-15
0          0.2   0.4        0.6   0.8       1         1.2     1.4       1.6        1.8          2
(b)                                                                           Time (s)

> **Figure 8. Time histories of the (a) vertical and (b) horizontal velocities during a cast when the path of the rod tip is**
prescribed: —, velocity at the tip of the fly rod; - - ; velocity at the free end.

horizontal velocity of 10 m=s (negative) as shown in Fig. 8, compared to a velocity of 30 m=s for
the full-scale casts studied in Ref. [5]. We also notice that the peak vertical velocity of the yarn rod
is reduced by approximately a factor of three compared to a full scale rod. The second phase


0.2
Phase 1         Phase 2
Phase 3

0.15

0.1
Tension (N)

0.05

0

-0.05
0     0.2     0.4       0.6        0.8      1       1.2   1.4   1.6   1.8   2
Time (s)

> **Figure 9. Time history of the line tension at the rod tip when the path of the rod tip is prescribed.**

occurs between t = 0:45 and 0:7 s; and the loop forms during the abrupt deceleration of the rod
tip. The tension becomes negative, and the compression indicated in Fig. 9 lasts far longer than
that observed for the full-length casts in Ref. [5]. The loop propagates during the last phase of the
first forward cast, between t = 0:7 and 0:8 s: It opens slowly as the rod begins to move backwards
as the back cast is initiated.
The analysis of the cast performed with a yarn rod confirms that the dynamic behavior of
the yarn rod is qualitatively similar to that of a fly line for full-scale fly casting. However, the
analysis also shows that the dynamics of the yarn rod is approximatively three times slower than
the full-scale fly rod, making it far easier to capture the rod and line motion on standard VHS
video.

### 5.2. Coupled fly line/fly rod model

We now turn our attention to evaluating the coupled dynamics of the fly rod and the fly line as
a system, and comparing results with the previous (uncoupled) model of the line alone. The
parameters used in the numerical simulation of the forward cast are given in Table 2, and the
measured angular velocity that constitutes the boundary condition at the caster's hand is depicted
in Fig. 10.
Fig. 11 shows that the horizontal and vertical velocity components of the tip of the rod are in
good agreement for the two models. Thus, the added dynamics of the rod leads to nearly the same
motion of the rod tip as measured by the experiment (input to the uncoupled model of line).
Therefore, the coupled model described above properly captures the dynamics of the fly rod.
Fig. 12 shows the time history of the velocities computed using the coupled model. The solid line
on Fig. 13 represents the time history of the tension at the rod tip predicted by the coupled model.

**Table 2. Complementary data used for simulation of coupled rod/line model of Section 5.2**

| Parameter | Symbol | Value |
|---|---:|---:|
| Bending stiffness of the fly rod | $E_r$ | $500\times10^9\,\mathrm{N/m^2}$ |
| Inner diameter of the fly rod | $d_{ir}$ | $0.0022\,\mathrm m$ |
| Outer diameter of the fly rod | $d_{or}$ | $0.0029\,\mathrm m$ |
| Length of the fly rod | $l_r$ | $1.22\,\mathrm m$ |
| Density of the fly rod | $\rho_r$ | $2023.32\,\mathrm{kg/m^3}$ |
| Coarse time step | $\Delta t_1$ | $0.01\,\mathrm s$ |
| Fine time step | $\Delta t_2$ | $0.01\,\mathrm s$ |
| Spatial step | $\Delta s$ | $0.0056\,\mathrm m$ |

6

4
Angular velocity6

4
Angular velocity of the rod (rad/s)

2

0

-2

-4

-6

0   0.5     1      1.5         2   2.5   3
Time (s)

> **Figure 10. Experimental angular velocity of the fly rod used as a boundary condition to simulate the motion of the**
“yarn rod”.

From Figs. 12 and 13, we can identify the same three phases described in Section 5.1. These
include: a near rigid-body motion of the fly line, loop formation, and loop propagation and
turnover. Fig. 13 also shows that the predictions of the tension at the rod tip are very similar for
the coupled and uncoupled model (the coupled model predicts a slightly higher tension and
sharper compression peaks relative to the uncoupled model). Fig. 14 shows the numerical
prediction of the positions of the fly rod and fly line for 9 selected times during one forward cast.
The results of Figs. 7 and 14 appear to be very similar, but the loop is slightly wider and directed
upwards in Fig. 14, whereas it is directed horizontally in Fig. 7.
The qualitative comparisons above between the coupled and uncoupled models shows that the
main features of loop dynamics are the same. Modest differences do arise, especially in the


6

Vertical velocity (m/s)
4

2

0

-2

-4

0         0.5          1       1.5              2         2.5         3         3.5
(a)                                                                  Time (s)

10
Horizontal velocity (m/s)

5

0

-5

-10
0         0.5          1       1.5              2         2.5         3         3.5
(b)                                                                             Time (s)

> **Figure 11. (a) Vertical and (b) horizontal velocities of the rod tip for the two models: —, coupled model; - - ; uncoupled**
model.

15

10
Vertical velocity (m/s)

5

0

-5

-10
0    0.2         0.4    0.6   0.8        1           1.2   1.4   1.6       1.8     2
(a)                                                                             Time (s)

15
Horizontal velocity (m/s)

10

5

0

-5

-10

-15
0    0.2         0.4    0.6   0.8        1           1.2   1.4   1.6       1.8     2
(b)                                                                             Time (s)

> **Figure 12. Time history of the (a) vertical and (b) horizontal velocities for the coupled model: —, rod tip; - - ; free end.**

geometry of the loop and these ultimately derive from modest differences in the measured versus
the computed rod tip velocities used in the uncoupled and coupled models, respectively. It should
also be noted that the lightweight yarn rod introduces very modest rod bending compared to what
is observed in casting a full-scale fly rod with a significant length of fly line.

0.2

0.15

0.1
Tension (N)

0.05

0

-0.05

0   0.2        0.4     0.6     0.8        1         1.2         1.4   1.6   1.8         2
Time (s)

> **Figure 13. Time history of the tension at the rod tip path for the two models: — coupled model; - - ; uncoupled model.**

2

1.5
Vertical position (m)

1

0.5

0

-2.5          -2            -1.5            -1               -0.5         0           0.5
Horizontal position (m)

> **Figure 14. Numerical prediction of the forward cast obtained from the coupled model using experimental data for the**
rigid-body rotation of the fly rod.

### 5.3. Comparisons with experiment

Images of the fly rod and fly line system were extracted from a digital movie file produced from
recorded video. Superimposing frames from the movie file with the output of the numerical


Simulation

Simulation

Experiment
Experiment

(a)                                          (b)

Simulation                                Simulation

Experiment                  Experiment

(c)                                          (d)

> **Figure 15. Comparison of numerical solution of the coupled model with experimental data during (a) loop formation,**
(b) and (c) loop propagation, and (d) loop turn over.

simulation leads to the results shown in Figs. 15(a)-(d). Fig. 15 corresponds to the important
phases of the loop dynamics; namely, loop formation, loop propagation and final loop turn over.
Fig. 15(a) corresponds to the time when the loop has just formed. The positions of the fly rods
match very well. The “numerical” loop is smoother than the real loop, especially near the fly rod,
where the prediction of the position of the fly line differs by 10 cm from the actual position. Recall
that the fly line has an overall length of 213 cm; and therefore the 10 cm error at the end of the line
represents an error of slightly less than 5% when compared to the length of the line. This part of
the loop corresponds to the largest discrepancy between the predicted and measured position,
while the smallest discrepancy occurs in the upper part of the loop. Overall, this graph shows that
the simulation and experiment measurements agree within 5% (by the error measure proposed
above), and thus, we conclude that the numerical model properly captures the physics of the loop
formation.
The next two figures correspond to two different instants of time during the loop propagation
phase. Fig. 15(b) shows a slight difference in fly rod positions. The overall shapes of the loops are
similar with the largest discrepancies appearing on the upper part of the loop where they are less
than 6% when compared to the length of the line. Fig. 15(c) shows better agreement between the
numerical model and the video image. The position of the fly rod is predicted very accurately. The
shapes of the loop are in excellent agreement, except for a small portion near the fly rod and for
the very end of the fly line (to within 7%). These two figures also demonstrate that the numerical
model accurately captures the physics of the loop propagation.
Fig. 15(d) shows the final stage of loop turn over. The predicted position of the fly rod and the
shape of the loop compare very well with the video image (to within 10% for the loop). As in
Fig. 15(a), the “numerical loop'' appears smoother than the experimental loop, and the largest

discrepancy occurs in the middle part of the fly line. Here again, we assert that the physics of the
loop turn over is well captured by the model.
The discrepancies between the experimental data and numerical results arise from three main
sources: the numerical model does not account for the unavoidable and slight translation of the
caster's hand, the drag coefficients have been estimated without prior measurement, and the
bending stiffness of the line has also been estimated without prior measurement.

## 6. Summary and conclusion

This paper presents a model that captures the flexible motion of the fly rod, and the coupling
between the dynamics of the fly rod and of the fly line. The fly rod is described as a flexible Euler-
Bernoulli beam possessing two degrees of freedom: a rigid-body mode and a flexible-body mode.
The line and the rod are coupled through the boundary conditions at their junction.
An experiment is described that was used to measure the positions of a fly rod and the shape of
the fly line during an overhead cast. The experiment was performed using a “yarn rod”, where a
line made of yarn is attached to the top half of a fly rod. Two sets of numerical results are
presented for casting with this yarn rod. The first set uses an uncoupled model composed of the fly
line alone. These results clearly demonstrate the main features of a full-scale system including loop
formation, loop propagation, and loop turnover. The second set considers a coupled model for
the fly rod and fly line system. Comparison with the uncoupled model reveals the same major
features of loop dynamics. Finally, the simulations obtained from the coupled model are
compared with images obtained by video for casts with the yarn rod. This comparison reveals that
the shape of the loop during formation, propagation and turnover are predicted by the numerical
model to within 10% of the measured loop geometry.

## Acknowledgements

The authors wish to acknowledge the help of Professor James Ashton-Miller, Bing-Shiang
Yang and Mari Endo from the University of Michigan Biomechanics Laboratory for their
assistance in measuring the dynamics of a yarn rod. We thank Mr. Bruce Richards of Scientific
Anglers Inc. for the supplies and the many insightful technical discussions. We are grateful for the
support from the Rackham Predoctoral Fellowship offered by the Horace H. Rackham School of
Graduate Studies at the University of Michigan. The authors are also grateful for the insightful
comments offered by the reviewers of Ref. [5] who suggested extensions of our work as contained
herein. Finally, the second author is grateful to Dr. Kim A. Eagle of the University of Michigan
for his inspirational fly casting.

## References

[1] B. Kreh, Modern Fly Casting Method, Odysseus Editions, Birmingham, AL, 1991.
[2] M.K. Krieger, The Essence of Flycasting, Club Pacific, San Francisco, CA, 1987.
[3] J. Wulff, Joan Wulff's Fly Casting Techniques, The Lyons Press, New York, 1987.


[4] D. Phillips, The Technology of Fly Rods, Frank Amato Publications, Portland, OR, 2000.
[5] C. Gatti-Bono, N.C. Perkins, Physical and numerical modeling of the dynamic behavior of a fly line, Journal of
Sound and Vibration 255 (3) (2002) 555-577.
[6] G.A. Spolek, http://www.me.pdx.edu/graig/cast-ref.htm.
[7] G.A. Spolek, The mechanics of flycasting: the fly line, American Journal of Physics 54 (9) (1986) 832-835.
[8] S. Lingard, Note on the aerodynamics of a fly line, American Journal of Physics 56 (8) (1988) 756-757.
[9] J.M. Robson, The physics of flycasting, American Journal of Physics 58 (3) (1990) 234-240.
[10] C.S. Gatti, N.C. Perkins, Numerical analysis of flycasting mechanics, ASME Bioengineering Conference, BED-Vol. 50,
Snowbird, UT, 2001, pp. 277-278.
[11] G.A. Solek, Fly rod performance, in: J.M. Tarbell (Ed.), Advances in Bioengineering, Vol. 26, ASME, New York,
1993, pp. 251-254.
[12] J.A. Hoffmann, M.R. Hooper, Fly rod performance and line selection, Proceedings of DETC'97, Sacramento, CA,
1997.
[13] A. Yigit, R.A. Scott, A.G. Ulsoy, Flexural motion of a radially rotating beam attached to a rigid body, Journal of
Sound and Vibration 121 (2) (1988) 201-210.
[14] M. Krieger, The Essence of Flycasting, video produced by Club Pacific, 1985.
[15] C.S.C. Gatti, Numerical Simulations of Large Deformation Cable Dynamics, Ph.D. Dissertation, University of
Michigan, 2002.
[16] Y. Sun, Modeling and Simulation of Low-Tension Oceanic Cable/Body Deployment, Ph.D. Dissertation,
University of Connecticut, 1996.
[17] Y. Sun, Nonlinear Response of Cable/Lumped-Body System by Direct Integration Method with Suppression,
Masters Thesis, Oregon State University, 1992.
